using mk8.email.Contracts.Enums;

namespace mk8.email.Contracts.DTOs;

public sealed record MailDomainSummaryDTO(
    Guid Id,
    string Domain,
    string Company,
    bool IsActive,
    int AccountCount,
    string? CatchAllTarget);

public sealed record MailAccountSummaryDTO(
    Guid UserId,
    string Address,
    UserRole Role,
    bool IsActive,
    bool IsCatchAllTarget,
    DateTime CreatedAt);

public sealed record AdministrationResult(bool Succeeded, string Message, Guid? EntityId = null);

public sealed record MailSystemStatusDTO(
    string State,
    DateTimeOffset? CheckedAt,
    int? QueueCount,
    long? OldestQueuedMessageSeconds,
    int? MailStorageUsedPercent,
    long? BackupExportAgeSeconds,
    long? ClamSignatureAgeSeconds,
    long? MailCertificateRemainingSeconds,
    long? AdminCertificateRemainingSeconds,
    int ErrorCount)
{
    public static MailSystemStatusDTO Unavailable { get; } = new(
        "unavailable",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        0);
}
