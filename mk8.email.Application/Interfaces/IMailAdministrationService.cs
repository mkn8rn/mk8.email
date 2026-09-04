using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Enums;

namespace mk8.email.Application.Interfaces;

public interface IMailAdministrationService
{
    Task<AdministrationResult> EnsureDomainAsync(
        string companyName,
        string domain,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> CreateAccountAsync(
        string address,
        string password,
        UserRole role,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetCatchAllAsync(
        string domain,
        string targetAddress,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetDomainActiveAsync(
        string domain,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> SetAccountActiveAsync(
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<AdministrationResult> ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailDomainSummaryDTO>> GetDomainsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailAccountSummaryDTO>> GetAccountsAsync(
        CancellationToken cancellationToken = default);
}
