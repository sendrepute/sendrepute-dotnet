using MimeKit;

namespace SendRepute.MailKit;

public enum EnforcementPolicy
{
    Advisory,
    BlockAtOrAboveThreshold
}

public enum FailurePolicy
{
    PreserveSend,
    BlockSend
}

public enum MessageCategory
{
    Ordinary,
    CriticalSecurityOrAccount
}

public sealed record SendReputeOptions
{
    public bool Enabled { get; init; }
    public bool PaidAnalysisConsent { get; init; }
    public string ApiToken { get; init; } = "";
    public double SpamProbabilityThreshold { get; init; } = 0.8;
    public EnforcementPolicy Enforcement { get; init; } = EnforcementPolicy.Advisory;
    public FailurePolicy OnFailure { get; init; } = FailurePolicy.PreserveSend;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record AnalysisConsent
{
    public bool PaidAnalysisConsent { get; init; }
    public MessageCategory Category { get; init; } = MessageCategory.Ordinary;
    public bool CriticalMessagePaidAnalysisConsent { get; init; }
}

public sealed record ClassificationReceipt(
    string RequestId,
    string Model,
    string Label,
    double SpamProbability,
    string Confidence,
    IReadOnlyList<ClassificationReason> Reasons,
    IReadOnlyList<string> FlaggedTerms,
    long? FlaggedTermCount,
    IReadOnlyList<string> AnalyzedFields,
    string ModelVersion,
    DateTimeOffset AnalyzedAt,
    ContentAuditResult? ContentAudit,
    BillingReceipt Billing);

public sealed record ClassificationReason(string Signal, string Detail, double Weight);

public sealed record ContentAuditResult(
    long Score,
    string Grade,
    string Summary,
    ContentAuditCounts Counts,
    long TotalIssues,
    long CriticalCount,
    long WarningCount,
    long SuggestionCount,
    IReadOnlyList<ContentAuditIssue> Issues,
    IReadOnlyList<ContentAuditGoodPractice> GoodPractices,
    IReadOnlyList<string>? HomoglyphTerms,
    bool InputTruncated);

public sealed record ContentAuditCounts(long Words, long Links, long Images, long TriggerPhrases);

public sealed record ContentAuditIssue(
    string Code,
    string Category,
    string Severity,
    long Deduction,
    string Evidence);

public sealed record ContentAuditGoodPractice(string Code, string Category);

public sealed record BillingReceipt(
    bool Replayed,
    long ChargedMillicents);

public sealed record AnalysisOutcome(
    bool Analyzed,
    bool ShouldBlock,
    ClassificationReceipt? Receipt,
    string? FailureCode);

public interface IMailTransport
{
    void Send(MimeMessage message, CancellationToken cancellationToken = default);
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
}

public sealed class SendReputeException : Exception
{
    public SendReputeException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed class SendBlockedException : Exception
{
    public SendBlockedException(string message, AnalysisOutcome outcome) : base(message) => Outcome = outcome;
    public AnalysisOutcome Outcome { get; }
}