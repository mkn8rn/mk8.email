namespace mk8.email.Contracts.DTOs;

public record CreateInboxRequestDTO(
    string Name,
    Guid AddressId,
    Guid? ForUserId = null,
    Guid? AliasForInboxId = null);
