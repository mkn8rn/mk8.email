using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface IMailSystemStatusService
{
    Task<MailSystemStatusDTO> GetStatusAsync(CancellationToken cancellationToken = default);
}
