namespace mk8.email.Contracts.DTOs;

public record GlobalLimitsDTO(
    Guid Id,
    int DefaultMaxDomainsPerCompany,
    int DefaultMaxInboxesPerCompany,
    int DefaultMaxInboxesPerDomain);
