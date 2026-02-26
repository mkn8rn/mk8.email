using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface ICompanyService
{
    Task<CompanyDTO?> CreateCompanyAsync(Guid userId, CreateCompanyRequestDTO request);
    Task<IReadOnlyList<CompanyDTO>> GetAllCompaniesAsync();
    Task<CompanyDTO?> GetCompanyAsync(Guid companyId);

    Task<GlobalConfigDTO> GetGlobalConfigAsync();
    Task<GlobalConfigDTO?> UpdateGlobalConfigAsync(Guid userId, GlobalConfigDTO config);
    Task<GlobalLimitsDTO> GetGlobalLimitsAsync();
    Task<GlobalLimitsDTO?> UpdateGlobalLimitsAsync(Guid userId, GlobalLimitsDTO limits);

    Task<CompanyConfigDTO?> GetCompanyConfigAsync(Guid userId, Guid companyId);
    Task<CompanyConfigDTO?> UpdateCompanyConfigAsync(Guid userId, Guid companyId, bool allowUserRegistration);
    Task<CompanyLimitsDTO?> GetCompanyLimitsAsync(Guid userId, Guid companyId);
    Task<CompanyLimitsDTO?> UpdateCompanyLimitsAsync(Guid userId, Guid companyId, int? maxDomains, int? maxInboxes, int? maxInboxesPerDomain);
}
