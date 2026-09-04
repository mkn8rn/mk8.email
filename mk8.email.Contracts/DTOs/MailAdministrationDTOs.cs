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
