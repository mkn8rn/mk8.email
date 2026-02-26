namespace mk8.email.Contracts.DTOs;

public record InboxDTO(
    Guid Id,
    string Name,
    Guid AddressId,
    string Domain,
    Guid OwnerId,
    Guid? AliasForInboxId,
    DateTime CreatedAt);
