namespace mk8.email.Contracts.DTOs;

public record CompanyDTO(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAt);
