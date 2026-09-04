using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface IDatabaseInitializationService
{
    Task<AdministrationResult> InitializeEmptyDatabaseAsync(
        CancellationToken cancellationToken = default);
}
