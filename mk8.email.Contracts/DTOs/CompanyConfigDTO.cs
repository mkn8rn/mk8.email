namespace mk8.email.Contracts.DTOs;

public record CompanyConfigDTO(
    Guid Id,
    Guid CompanyId,
    bool AllowUserRegistration);
