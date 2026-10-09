using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MimeKit;
using SendRepute.MailKit;

var tests = new (string, Func<Task>)[]
{
    ("defaults require opt-in", DefaultsRequireOptIn),
    ("customer services keep authorization and fixed routes", CustomerServices),
    ("public request and nested response contract", PublicContract),
    ("all MIME alternatives survive normalizer hazards", AlternativesAreCovered),
    ("single-fragment global base64 survives actual normalizer", SingleFragmentBase64),
    ("required response fields and enums are validated", ResponseValidation),
    ("blocking preserves original message and transport options", BlockingAndPreservation),
    ("advisory failure preserves send", AdvisoryFailure),
    ("critical mail requires independent consent", CriticalConsent),
    ("redirects are refused without retry", RedirectRefused),
    ("cancellation prevents transport", CancellationPreventsTransport),
};

foreach (var (name, test) in tests)
{
    await test();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"PASS {tests.Length} exact tests");

static async Task DefaultsRequireOptIn()
{
    var handler = new MockHandler(_ => Ok());
    using var client = new SendReputeClient(new SendReputeOptions(), handler);
    await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", new()));
    Equal(0, handler.Calls);
}

static async Task CustomerServices()
{
    var handler = new MockHandler(request =>
    {
        Equal("https://www.sendrepute.com/api/v1/account", request.RequestUri!.AbsoluteUri);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"account\":\"fixture\"}") };
    });
    using var client = new SendReputeClient(new SendReputeOptions { ApiToken = "offline-fixture" }, handler);
    await Throws<SendReputeException>(() => client.RequestServiceAsync("customerPurchaseVip"));
    await Throws<SendReputeException>(() => client.RequestServiceAsync("arbitraryUrl"));
    Equal(0, handler.Calls);
    var result = await client.RequestServiceAsync("customerGetAccount");
    Equal("fixture", result.GetProperty("account").GetString()!);
    Equal(1, handler.Calls);
}

static async Task PublicContract()
{
    string? json = null;
    var handler = new MockHandler(request =>
    {
        Equal("https://www.sendrepute.com/api/v1/classify", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Ok();
    });
    using var client = Client(handler);
    var receipt = await client.ClassifyAsync("Display Team", "Subject", "Body", Consent());
    using var payload = JsonDocument.Parse(json!);
    Equal("Display Team", payload.RootElement.GetProperty("sender").GetString());
    Equal(3, payload.RootElement.EnumerateObject().Count());
    Equal("req_fixture", receipt.RequestId);
    Equal("thor", receipt.Model);
    Equal(0.42, receipt.SpamProbability);
    Equal(false, receipt.Billing.Replayed);
    Equal("fixture-v1", receipt.ModelVersion);
    Equal(0, receipt.Reasons.Count);
    Equal(3, receipt.AnalyzedFields.Count);
    Equal(1L, receipt.FlaggedTermCount);
    Equal("A", receipt.ContentAudit!.Grade);
    Equal(12L, receipt.ContentAudit.Counts.Words);
    Equal("links", receipt.ContentAudit.Issues[0].Category);
    Equal("subject", receipt.ContentAudit.GoodPractices[0].Category);
}

static async Task AlternativesAreCovered()
{
    string? body = null;
    var handler = new MockHandler(request =>
    {
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        body = json.RootElement.GetProperty("body").GetString();
        return Ok();
    });
    const string raw = """
        From: Display Team <sender@example.test>
        To: Recipient <recipient@example.test>
        Subject: Subject
        MIME-Version: 1.0
        Content-Type: multipart/alternative; boundary=parts

        --parts
        Content-Type: text/plain; charset=utf-8
        Content-Transfer-Encoding: quoted-printable

        benign { spam offer malicious } tail
        Content-Transfer-Encoding: base64

        PHN0eWxlPnNwYW0gb2ZmZXIgaGlkZGVuPC9zdHlsZT4=
        --fake
        --parts
        Content-Type: text/html; charset=utf-8
        Content-Transfer-Encoding: base64

        PHA+bWFsaWNpb3VzIGxhdGVyIGZyYWdtZW50PC9wPg==
        --parts--
        """;
    using var rawStream = new MemoryStream(Encoding.UTF8.GetBytes(raw.Replace("\n", "\r\n", StringComparison.Ordinal)));
    var message = MimeMessage.Load(rawStream);
    var transport = new MockTransport();
    using var client = Client(handler);
    var adapter = new SendReputeMailAdapter(transport, client, Enabled());
    await adapter.AnalyzeAsync(message, Consent());
    Contains("benign", body!);
    Contains("spam offer malicious", body!);
    Contains("malicious later fragment", body!);
    Contains("DISPLAYED ALTERNATIVE BOUNDARY", body!);
    if (body!.Contains("=3C", StringComparison.Ordinal) || body.Contains("<script", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Normalizer-ambiguous syntax reached the paid payload.");
    Equal(1, handler.Calls);
    RunNormalizerProbe(body!, ["benign", "spam offer malicious", "tail", "malicious later fragment"]);
}

static async Task SingleFragmentBase64()
{
    var hidden = "<style>" + new string('x', 520) + " spam offer</style>";
    var displayedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(hidden));
    string? body = null;
    var handler = new MockHandler(request =>
    {
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        body = json.RootElement.GetProperty("body").GetString();
        return Ok();
    });
    using var client = Client(handler);
    var adapter = new SendReputeMailAdapter(new MockTransport(), client, Enabled());
    await adapter.AnalyzeAsync(Message(new TextPart("plain") { Text = displayedBase64 }), Consent());
    RunNormalizerProbe(body!, [displayedBase64[..40], "DISPLAYED CONTENT END"]);
    Equal(1, handler.Calls);
}

static async Task ResponseValidation()
{
    var invalidModel = JsonResponse("""{"requestId":"req_fixture","model":"thor-lookalike","result":{"label":"inbox","spamProbability":0.42,"confidence":"high","reasons":[],"flaggedTerms":[],"analyzedFields":["sender","subject","body"],"modelVersion":"fixture-v1","analyzedAt":"2025-01-01T00:00:00Z"},"billing":{"chargedMillicents":10,"replayed":false}}""");
    using (var client = Client(new MockHandler(_ => invalidModel)))
    {
        var error = await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", Consent()));
        Equal("invalid_response", error.Code);
    }
    var missingRequired = JsonResponse("""{"requestId":"req_fixture","model":"thor","result":{"label":"inbox","spamProbability":0.42,"confidence":"high","reasons":[],"flaggedTerms":[],"analyzedFields":["sender","subject","body"],"modelVersion":"fixture-v1"},"billing":{"chargedMillicents":10,"replayed":false}}""");
    using (var client = Client(new MockHandler(_ => missingRequired)))
    {
        var error = await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", Consent()));
        Equal("invalid_response", error.Code);
    }
    var stringCountNode = JsonNode.Parse(ValidResponseJson())!;
    stringCountNode["result"]!["flaggedTermCount"] = "1";
    using (var client = Client(new MockHandler(_ => JsonResponse(stringCountNode.ToJsonString()))))
    {
        var error = await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", Consent()));
        Equal("invalid_response", error.Code);
    }
    var falseAuditNode = JsonNode.Parse(ValidResponseJson())!;
    falseAuditNode["result"]!["contentAudit"] = false;
    using (var client = Client(new MockHandler(_ => JsonResponse(falseAuditNode.ToJsonString()))))
    {
        var error = await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", Consent()));
        Equal("invalid_response", error.Code);
    }
}

static async Task BlockingAndPreservation()
{
    var message = Message(new TextPart("plain") { Text = "body" });
    var originalBody = message.Body;
    var transport = new MockTransport();
    var options = Enabled() with { Enforcement = EnforcementPolicy.BlockAtOrAboveThreshold, SpamProbabilityThreshold = .4 };
    using var client = new SendReputeClient(options, new MockHandler(_ => Ok()));
    var adapter = new SendReputeMailAdapter(transport, client, options);
    await Throws<SendBlockedException>(() => adapter.AnalyzeThenSendAsync(message, Consent(), new CancellationTokenSource().Token));
    Equal(0, transport.AsyncCalls);
    Same(originalBody, message.Body);
}

static async Task AdvisoryFailure()
{
    var message = Message(new TextPart("plain") { Text = "body" });
    var transport = new MockTransport();
    var options = Enabled() with { Enforcement = EnforcementPolicy.Advisory, OnFailure = FailurePolicy.PreserveSend };
    using var client = new SendReputeClient(options, new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)));
    var adapter = new SendReputeMailAdapter(transport, client, options);
    var outcome = await adapter.AnalyzeThenSendAsync(message, Consent());
    Equal(false, outcome.Analyzed);
    Equal("http_402", outcome.FailureCode);
    Same(message, transport.LastMessage);
}

static async Task CriticalConsent()
{
    var handler = new MockHandler(_ => Ok());
    using var client = Client(handler);
    await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Reset", "code", Consent() with
    {
        Category = MessageCategory.CriticalSecurityOrAccount
    }));
    Equal(0, handler.Calls);
}

static async Task RedirectRefused()
{
    var handler = new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
    {
        Headers = { Location = new Uri("https://example.invalid/") }
    });
    using var client = Client(handler);
    var exception = await Throws<SendReputeException>(() => client.ClassifyAsync("Team", "Subject", "Body", Consent()));
    Equal("redirect_refused", exception.Code);
    Equal(1, handler.Calls);
}

static async Task CancellationPreventsTransport()
{
    var transport = new MockTransport();
    var handler = new MockHandler(async (_, token) =>
    {
        await Task.Delay(TimeSpan.FromMinutes(1), token);
        return Ok();
    });
    using var client = Client(handler);
    var adapter = new SendReputeMailAdapter(transport, client, Enabled());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => adapter.AnalyzeThenSendAsync(Message(new TextPart("plain") { Text = "body" }), Consent(), cancellation.Token));
    Equal(0, transport.AsyncCalls);
}

static SendReputeOptions Enabled() => new()
{
    Enabled = true,
    PaidAnalysisConsent = true,
    ApiToken = "server-test-token",
    RequestTimeout = TimeSpan.FromSeconds(2)
};

static SendReputeClient Client(HttpMessageHandler handler) => new(Enabled(), handler);
static AnalysisConsent Consent() => new() { PaidAnalysisConsent = true };
static MimeMessage Message(MimeEntity body)
{
    var message = new MimeMessage { Subject = "Subject", Body = body };
    message.From.Add(new MailboxAddress("Display Team", "sender@example.test"));
    message.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
    return message;
}

static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
{
    Content = new StringContent(ValidResponseJson(), Encoding.UTF8, "application/json")
};
static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};
static string ValidResponseJson() =>
    """
    {
      "requestId": "req_fixture",
      "model": "thor",
      "result": {
        "label": "inbox",
        "spamProbability": 0.42,
        "flaggedTermCount": 1,
        "confidence": "high",
        "reasons": [],
        "flaggedTerms": [],
        "analyzedFields": ["sender", "subject", "body"],
        "modelVersion": "fixture-v1",
        "analyzedAt": "2025-01-01T00:00:00Z",
        "contentAudit": {
          "score": 96,
          "grade": "A",
          "summary": "looks_good",
          "counts": {"words": 12, "links": 1, "images": 0, "triggerPhrases": 0},
          "totalIssues": 1,
          "criticalCount": 0,
          "warningCount": 0,
          "suggestionCount": 1,
          "issues": [
            {"code": "link_review", "category": "links", "severity": "suggestion", "deduction": 4, "evidence": "example"}
          ],
          "goodPractices": [{"code": "clear_subject", "category": "subject"}],
          "homoglyphTerms": ["term"],
          "inputTruncated": false
        }
      },
      "billing": {"chargedMillicents": 10, "replayed": false}
    }
    """;

static void RunNormalizerProbe(string payload, string[] expected)
{
    var root = Path.GetFullPath(Environment.CurrentDirectory);
    while (!File.Exists(Path.Combine(root, "artifacts", "api-server", "src", "lib", "classifier-features.ts")))
    {
        root = Directory.GetParent(root)?.FullName
            ?? throw new Exception("Repository root for actual visibleEmailText probe was not found.");
    }
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };
    process.StartInfo.ArgumentList.Add("--experimental-strip-types");
    process.StartInfo.ArgumentList.Add("integrations/dotnet/tests/visible-email-text-probe.ts");
    process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)));
    process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expected))));
    process.Start();
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new Exception($"Actual visibleEmailText probe failed: {error}");
    if (string.IsNullOrWhiteSpace(output))
        throw new Exception("Actual visibleEmailText returned no displayed content.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}
static void Same(object? expected, object? actual)
{
    if (!ReferenceEquals(expected, actual)) throw new Exception("References differ.");
}
static void Contains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal)) throw new Exception($"Missing {expected}.");
}
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class MockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback;
    public int Calls { get; private set; }
    public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : this((request, _) => Task.FromResult(callback(request))) { }
    public MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => this.callback = callback;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return callback(request, cancellationToken);
    }
}

sealed class MockTransport : IMailTransport
{
    public int AsyncCalls { get; private set; }
    public MimeMessage? LastMessage { get; private set; }
    public void Send(MimeMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessage = message;
    }
    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AsyncCalls++;
        LastMessage = message;
        return Task.CompletedTask;
    }
}