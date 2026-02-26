namespace mk8.email.Contracts.DTOs;

public record CompanyLimitsDTO(
    Guid Id,
    Guid CompanyId,
    int? MaxDomains,
    int? MaxInboxes,
    int? MaxInboxesPerDomain);
