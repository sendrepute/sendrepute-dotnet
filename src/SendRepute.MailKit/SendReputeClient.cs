using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace SendRepute.MailKit;

public sealed class SendReputeClient : IDisposable
{
    public static readonly Uri Endpoint = new("https://www.sendrepute.com/api/v1/classify");
    private const int MaxPayloadBytes = 1_048_576;
    private readonly SendReputeOptions options;
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public SendReputeClient(SendReputeOptions options, HttpMessageHandler? handler = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        if (handler is null)
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = options.RequestTimeout,
                MaxResponseHeadersLength = 32,
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };
            ownsHttp = true;
        }
        http = new HttpClient(handler, ownsHttp) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ClassificationReceipt> ClassifyAsync(
        string sender,
        string subject,
        string body,
        AnalysisConsent consent,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            throw new SendReputeException("disabled", "SendRepute is disabled.");
        if (!options.PaidAnalysisConsent || !consent.PaidAnalysisConsent)
            throw new SendReputeException("paid_consent_required", "Configuration and this call must both consent to paid analysis.");
        if (consent.Category == MessageCategory.CriticalSecurityOrAccount && !consent.CriticalMessagePaidAnalysisConsent)
            throw new SendReputeException("critical_consent_required", "Critical security/account mail requires separate explicit paid-analysis consent.");
        if (string.IsNullOrEmpty(sender) || sender.Length > 320)
            throw new SendReputeException("invalid_sender", "Sender display name must contain 1 to 320 characters.");
        if (string.IsNullOrEmpty(subject) || subject.Length > 998)
            throw new SendReputeException("invalid_subject", "Subject must contain 1 to 998 characters.");
        if (string.IsNullOrEmpty(body) || Encoding.UTF8.GetByteCount(body) > 524_288)
            throw new SendReputeException("invalid_body", "Body must contain 1 to 524288 UTF-8 bytes.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { sender, subject, body });
        if (payload.Length > MaxPayloadBytes)
            throw new SendReputeException("input_too_large", "Encoded request exceeds 1 MiB.");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SendReputeException("deadline_exceeded", "SendRepute request deadline exceeded.");
        }
        catch (HttpRequestException)
        {
            throw new SendReputeException("network_error", "SendRepute request failed.");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new SendReputeException("redirect_refused", "SendRepute redirects are refused.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SendReputeException($"http_{(int)response.StatusCode}", "SendRepute rejected the request.");
            return ParseReceipt(bytes);
        }
    }

    private static ClassificationReceipt ParseReceipt(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            RequireObjectFields(root, ["requestId", "model", "result", "billing"], []);
            var requestId = RequiredString(root, "requestId", 128);
            if (requestId.Length == 0) throw new JsonException();
            var model = RequiredString(root, "model");
            if (model is not ("thor" or "theos" or "athena" or "odin" or "freya" or "hermes" or "ares" or "apollo"))
                throw new JsonException();
            var result = root.GetProperty("result");
            RequireObjectFields(
                result,
                ["label", "spamProbability", "confidence", "reasons", "flaggedTerms", "analyzedFields", "modelVersion", "analyzedAt"],
                ["flaggedTermCount", "contentAudit"]);
            var label = RequiredString(result, "label");
            var probability = result.GetProperty("spamProbability").GetDouble();
            var confidence = RequiredString(result, "confidence");
            var reasons = ParseReasons(result.GetProperty("reasons"));
            var flaggedTerms = ParseStringArray(result.GetProperty("flaggedTerms"));
            long? flaggedTermCount = null;
            if (result.TryGetProperty("flaggedTermCount", out var flaggedTermCountElement))
                flaggedTermCount = RequiredInteger(flaggedTermCountElement, 0);
            var analyzedFields = ParseStringArray(result.GetProperty("analyzedFields"));
            var modelVersion = RequiredString(result, "modelVersion");
            var analyzedAtText = RequiredString(result, "analyzedAt");
            if (!DateTimeOffset.TryParse(
                    analyzedAtText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var analyzedAt))
                throw new JsonException();
            ContentAuditResult? contentAudit = null;
            if (result.TryGetProperty("contentAudit", out var contentAuditElement))
                contentAudit = ParseContentAudit(contentAuditElement);
            var billing = root.GetProperty("billing");
            RequireObjectFields(billing, ["chargedMillicents", "replayed"], []);
            var replayed = billing.GetProperty("replayed").GetBoolean();
            var chargedMillicents = billing.GetProperty("chargedMillicents").GetInt64();
            if (!double.IsFinite(probability)
                || label is not ("inbox" or "spam")
                || confidence is not ("low" or "medium" or "high"))
                throw new JsonException();
            if (chargedMillicents < 0)
                throw new JsonException();
            return new(
                requestId, model, label, probability, confidence, reasons,
                flaggedTerms, flaggedTermCount, analyzedFields, modelVersion, analyzedAt,
                contentAudit,
                new(replayed, chargedMillicents));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SendReputeException("invalid_response", "SendRepute returned an invalid response.");
        }
    }

    private static string RequiredString(JsonElement parent, string name, int maxLength = int.MaxValue)
    {
        var value = parent.GetProperty(name).GetString();
        return value is null || value.Length > maxLength ? throw new JsonException() : value;
    }

    private static IReadOnlyList<ClassificationReason> ParseReasons(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException();
        var values = new List<ClassificationReason>();
        foreach (var item in array.EnumerateArray())
        {
            RequireObjectFields(item, ["signal", "detail", "weight"], []);
            var weight = item.GetProperty("weight").GetDouble();
            if (!double.IsFinite(weight)) throw new JsonException();
            values.Add(new(RequiredString(item, "signal"), RequiredString(item, "detail"), weight));
        }
        return values;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException();
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var value = item.GetString();
            if (value is null) throw new JsonException();
            values.Add(value);
        }
        return values;
    }

    private static ContentAuditResult ParseContentAudit(JsonElement audit)
    {
        RequireObjectFields(
            audit,
            ["score", "grade", "summary", "counts", "totalIssues", "criticalCount",
                "warningCount", "suggestionCount", "issues", "goodPractices", "inputTruncated"],
            ["homoglyphTerms"]);
        var score = RequiredInteger(audit.GetProperty("score"), 0, 100);
        var grade = RequiredString(audit, "grade");
        if (grade is not ("A" or "B" or "C" or "D" or "F")) throw new JsonException();
        var summary = RequiredString(audit, "summary");
        if (summary is not ("fix_critical" or "fix_warnings" or "review_suggestions" or "looks_good"))
            throw new JsonException();

        var countsElement = audit.GetProperty("counts");
        RequireObjectFields(countsElement, ["words", "links", "images", "triggerPhrases"], []);
        var counts = new ContentAuditCounts(
            RequiredInteger(countsElement.GetProperty("words"), 0),
            RequiredInteger(countsElement.GetProperty("links"), 0),
            RequiredInteger(countsElement.GetProperty("images"), 0),
            RequiredInteger(countsElement.GetProperty("triggerPhrases"), 0));

        var issuesElement = audit.GetProperty("issues");
        if (issuesElement.ValueKind != JsonValueKind.Array || issuesElement.GetArrayLength() > 50)
            throw new JsonException();
        var issues = new List<ContentAuditIssue>();
        foreach (var issue in issuesElement.EnumerateArray())
        {
            RequireObjectFields(issue, ["code", "category", "severity", "deduction", "evidence"], []);
            var category = AuditCategory(issue);
            var severity = RequiredString(issue, "severity");
            if (severity is not ("critical" or "warning" or "suggestion")) throw new JsonException();
            issues.Add(new(
                RequiredString(issue, "code"),
                category,
                severity,
                RequiredInteger(issue.GetProperty("deduction"), 0, 100),
                RequiredString(issue, "evidence", 200)));
        }

        var practicesElement = audit.GetProperty("goodPractices");
        if (practicesElement.ValueKind != JsonValueKind.Array || practicesElement.GetArrayLength() > 20)
            throw new JsonException();
        var practices = new List<ContentAuditGoodPractice>();
        foreach (var practice in practicesElement.EnumerateArray())
        {
            RequireObjectFields(practice, ["code", "category"], []);
            practices.Add(new(RequiredString(practice, "code"), AuditCategory(practice)));
        }

        IReadOnlyList<string>? homoglyphTerms = null;
        if (audit.TryGetProperty("homoglyphTerms", out var homoglyphElement))
        {
            if (homoglyphElement.ValueKind != JsonValueKind.Array || homoglyphElement.GetArrayLength() > 20)
                throw new JsonException();
            var terms = new List<string>();
            foreach (var item in homoglyphElement.EnumerateArray())
            {
                var term = item.GetString();
                if (term is null || term.Length > 120) throw new JsonException();
                terms.Add(term);
            }
            homoglyphTerms = terms;
        }

        return new(
            score,
            grade,
            summary,
            counts,
            RequiredInteger(audit.GetProperty("totalIssues"), 0),
            RequiredInteger(audit.GetProperty("criticalCount"), 0),
            RequiredInteger(audit.GetProperty("warningCount"), 0),
            RequiredInteger(audit.GetProperty("suggestionCount"), 0),
            issues,
            practices,
            homoglyphTerms,
            audit.GetProperty("inputTruncated").GetBoolean());
    }

    private static string AuditCategory(JsonElement value)
    {
        var category = RequiredString(value, "category");
        return category is "subject" or "content" or "links" or "structure" or "compliance"
            ? category
            : throw new JsonException();
    }

    private static long RequiredInteger(JsonElement value, long minimum, long maximum = long.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number < minimum || number > maximum)
            throw new JsonException();
        return number;
    }

    private static void RequireObjectFields(
        JsonElement value,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name) && !optional.Contains(property.Name))
                throw new JsonException();
            if (!seen.Add(property.Name)) throw new JsonException();
        }
        if (required.Any(field => !seen.Contains(field))) throw new JsonException();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return memory.ToArray();
            if (memory.Length + read > MaxPayloadBytes)
                throw new SendReputeException("response_too_large", "SendRepute response exceeds 1 MiB.");
            memory.Write(buffer, 0, read);
        }
    }

    private static void ValidateOptions(SendReputeOptions options)
    {
        if (options.SpamProbabilityThreshold is < 0 or > 1 || double.IsNaN(options.SpamProbabilityThreshold))
            throw new ArgumentOutOfRangeException(nameof(options.SpamProbabilityThreshold));
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
        if (options.Enabled && string.IsNullOrWhiteSpace(options.ApiToken))
            throw new ArgumentException("An API token is required when enabled.", nameof(options.ApiToken));
    }

    public void Dispose() => http.Dispose();
}