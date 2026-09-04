namespace mk8.email.Application.Interfaces;

public sealed record MailScanRequest(
    Guid QueueId,
    string EnvelopeSender,
    IReadOnlyList<string> Recipients,
    string RawMessage,
    string? ClientIp,
    string? Helo,
    string? AuthenticatedUser);

public sealed record MailScanResult(
    string Action,
    double Score,
    double RequiredScore,
    IReadOnlySet<string> Symbols,
    string AddedHeaders,
    bool IsMalware,
    bool IsTemporaryFailure);

public interface IMailScanner
{
    Task<MailScanResult> ScanAsync(
        MailScanRequest request,
        CancellationToken cancellationToken = default);
}
