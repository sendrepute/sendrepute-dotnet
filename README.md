> **Standalone source distribution:** this repository contains the integration runtime, documentation, and source packager. Upstream workspace/CMS/production-normalizer regression suites are deliberately not distributed here because they depend on private server code or isolated platform fixtures. Testing commands and historical verification evidence below describe upstream maintainer validation, not a self-contained test suite in this source-only checkout. No third-party registry publication is implied.

# SendRepute MailKit adapter

`SendRepute.MailKit` 0.1.0 is a .NET 8+ source package for explicit paid
pre-send classification of `MimeMessage` instances. It wraps an existing
application-owned `IMailTransport`; it does not install a global hook, open SMTP
connections, send by itself, or promise inbox placement.

## Install and configure

Reference `src/SendRepute.MailKit/SendRepute.MailKit.csproj`, or build the
allowlisted source ZIP locally. Restore resolves the real MailKit/MimeKit 4.18.0
dependency. No NuGet publication is performed.

```csharp
var options = new SendReputeOptions {
    Enabled = true,
    PaidAnalysisConsent = true,
    ApiToken = configuration["SENDREPUTE_API_KEY"]!, // server secret only
    Enforcement = EnforcementPolicy.Advisory,
    OnFailure = FailurePolicy.PreserveSend,
    SpamProbabilityThreshold = 0.80
};
using var client = new SendReputeClient(options);
var adapter = new SendReputeMailAdapter(existingTransport, client, options);

var outcome = await adapter.AnalyzeThenSendAsync(message, new AnalysisConsent {
    PaidAnalysisConsent = true,
    Category = MessageCategory.Ordinary
}, cancellationToken);
```

Both `Enabled` and paid consent default off. Each changed input may incur a new
charge; an identical request may return a replayed receipt. For password resets,
authentication codes, account alerts, and other critical security/account mail,
set `Category = CriticalSecurityOrAccount` and independently set
`CriticalMessagePaidAnalysisConsent = true`; otherwise no paid request occurs.

Advisory enforcement and preserve-send failure handling are independent
defaults. Blocking requires `BlockAtOrAboveThreshold`; failure blocking requires
`BlockSend`. Threshold is validated in the inclusive 0..1 range. Cancellation
is honored by analysis and the existing transport. The exact same `MimeMessage`
object is forwarded unchanged only after policy permits it.

The fixed endpoint is `https://www.sendrepute.com/api/v1/classify`. Requests
contain only sender display name, subject, and one safely framed body containing
every supported displayed `text/plain` and `text/html` MIME alternative.
Recipients and attachments are never uploaded. There are no retries or
redirects. TLS 1.2+ is required, deadlines are per call, and encoded requests
and responses are bounded to 1 MiB.

The body has non-base64 outer framing, and structural QP/base64, header, markup,
and CSS-brace characters are encoded as entities that the service decodes only
after its destructive normalization passes. Ordinary displayed content is
preserved while fragments cannot hide or consume later fragments.

## Verification evidence

Run offline after one dependency restore:

```sh
dotnet restore tests/SendRepute.MailKit.ExactTests/SendRepute.MailKit.ExactTests.csproj
dotnet run --no-restore --project tests/SendRepute.MailKit.ExactTests
dotnet run --project scripts/Package/Package.csproj
```

The executable exact tests use the actual MailKit/MimeKit package to build and
decode multipart messages, an in-memory HTTP handler with public-schema response
fixtures, and a mock `IMailTransport` (never SMTP). They verify the fixed public
path and sender/subject/body request, nested requestId/model/result/billing
response, all required typed fields, the optional integer flagged-term count,
and the complete recursively typed optional content audit, all-alternative coverage across
quoted-printable/base64-header/global-base64/markup/CSS-brace hazards,
off-by-default consent, independent policies, critical-mail consent, cancellation,
redirect refusal/no retry, and original object preservation. This is evidence
for .NET 8 and MailKit 4.18.0 only, not broad framework/version certification.
The hazard tests feed the exact captured adapter body through the actual
checked-in API `visibleEmailText` implementation via Node's TypeScript stripping
and `tests/visible-email-text-probe.ts`; a local repository checkout with a
current Node runtime is therefore required for those contract tests.
Response regressions specifically reject a string `flaggedTermCount` and a
boolean `contentAudit` before exposing a receipt.

The deterministic packager rejects links and includes only its source allowlist;
it excludes tokens, caches, build outputs, vendor packages, and tests from the
production ZIP. Output: `dist/sendrepute-mailkit-0.1.0-source.zip`.