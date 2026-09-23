using MimeKit;

namespace SendRepute.MailKit;

public sealed class SendReputeMailAdapter
{
    private readonly IMailTransport transport;
    private readonly SendReputeClient client;
    private readonly SendReputeOptions options;

    public SendReputeMailAdapter(IMailTransport transport, SendReputeClient client, SendReputeOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(
        MimeMessage message,
        AnalysisConsent consent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = MimeNormalizer.Normalize(message);
            var receipt = await client.ClassifyAsync(
                normalized.Sender, normalized.Subject, normalized.Body, consent, cancellationToken).ConfigureAwait(false);
            var block = options.Enforcement == EnforcementPolicy.BlockAtOrAboveThreshold
                && receipt.SpamProbability >= options.SpamProbabilityThreshold;
            return new(true, block, receipt, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SendReputeException exception)
        {
            if (options.OnFailure == FailurePolicy.BlockSend)
                return new(false, true, null, exception.Code);
            return new(false, false, null, exception.Code);
        }
    }

    public AnalysisOutcome AnalyzeThenSend(
        MimeMessage message,
        AnalysisConsent consent,
        CancellationToken cancellationToken = default)
    {
        var outcome = AnalyzeAsync(message, consent, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        ThrowIfBlocked(outcome);
        transport.Send(message, cancellationToken);
        return outcome;
    }

    public async Task<AnalysisOutcome> AnalyzeThenSendAsync(
        MimeMessage message,
        AnalysisConsent consent,
        CancellationToken cancellationToken = default)
    {
        var outcome = await AnalyzeAsync(message, consent, cancellationToken).ConfigureAwait(false);
        ThrowIfBlocked(outcome);
        await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static void ThrowIfBlocked(AnalysisOutcome outcome)
    {
        if (outcome.ShouldBlock)
            throw new SendBlockedException("Message was not passed to the configured transport.", outcome);
    }
}