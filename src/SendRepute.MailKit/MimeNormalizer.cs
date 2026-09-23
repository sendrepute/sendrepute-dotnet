using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace SendRepute.MailKit;

internal static partial class MimeNormalizer
{
    private const int MaxBodyUtf8Bytes = 524_288;

    public static (string Sender, string Subject, string Body) Normalize(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sender = message.Sender?.Name;
        if (string.IsNullOrWhiteSpace(sender))
            sender = message.From.Mailboxes.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(sender))
            throw new SendReputeException("unsupported_sender", "A sender display name is required; an address is not sent as a substitute.");

        var subject = message.Subject;
        if (string.IsNullOrWhiteSpace(subject))
            throw new SendReputeException("unsupported_subject", "A non-empty subject is required.");

        if (message.Body is null)
            throw new SendReputeException("unsupported_mime", "A MIME body is required.");

        var fragments = new List<string>();
        Visit(message.Body, fragments);
        if (fragments.Count == 0)
            throw new SendReputeException("unsupported_mime", "No supported displayed text body was found.");

        // A non-base64 prefix/suffix defeats whole-body autodetection. Structural
        // characters are entities decoded only after the server's base64, QP,
        // markup and CSS-brace removal passes, so no fragment can consume another.
        var body = "SENDREPUTE DISPLAYED CONTENT START "
            + string.Join(" SENDREPUTE DISPLAYED ALTERNATIVE BOUNDARY ", fragments.Select(Inert))
            + " SENDREPUTE DISPLAYED CONTENT END";
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyUtf8Bytes)
            throw new SendReputeException("input_too_large", "Normalized displayed text exceeds 524288 UTF-8 bytes.");
        return (sender.Trim(), subject, body);
    }

    private static void Visit(MimeEntity entity, List<string> fragments)
    {
        if (entity is MessagePart)
            throw new SendReputeException("unsupported_mime", "Nested messages are not classified.");

        if (entity is Multipart multipart)
        {
            var subtype = multipart.ContentType.MediaSubtype.ToLowerInvariant();
            if (subtype is "encrypted" or "signed")
                throw new SendReputeException("unsupported_mime", "Encrypted or signed multipart bodies cannot be safely inspected.");
            foreach (var child in multipart)
                Visit(child, fragments);
            return;
        }

        if (entity is TextPart text)
        {
            if (text.IsAttachment)
                return;
            var subtype = text.ContentType.MediaSubtype.ToLowerInvariant();
            var decoded = text.Text ?? "";
            if (subtype == "plain")
                fragments.Add(decoded);
            else if (subtype == "html")
                // Deliberately over-include script/hidden text instead of risking
                // a display heuristic swallowing adversarial content.
                fragments.Add(WebUtility.HtmlDecode(HtmlTags().Replace(decoded, " ")));
            else
                throw new SendReputeException("unsupported_mime", $"Displayed text/{subtype} is not supported.");
            return;
        }

        if (!entity.IsAttachment && entity.ContentDisposition?.Disposition != ContentDisposition.Attachment)
        {
            var mediaType = entity.ContentType.MediaType.ToLowerInvariant();
            if (mediaType != "image")
                throw new SendReputeException("unsupported_mime", $"Displayed {entity.ContentType.MimeType} content is not supported.");
        }
    }

    private static string Inert(string value)
    {
        var normalized = NullAndControlCharacters().Replace(value, " ");
        var encoded = WebUtility.HtmlEncode(normalized);
        return encoded
            .Replace("=", "&#61;", StringComparison.Ordinal)
            .Replace("+", "&#43;", StringComparison.Ordinal)
            .Replace("/", "&#47;", StringComparison.Ordinal)
            .Replace("{", "&#123;", StringComparison.Ordinal)
            .Replace("}", "&#125;", StringComparison.Ordinal)
            .Replace(":", "&#58;", StringComparison.Ordinal);
    }

    [GeneratedRegex("[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F\\u007F]")]
    private static partial Regex NullAndControlCharacters();

    [GeneratedRegex("<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex HtmlTags();
}