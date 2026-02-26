using mk8.email.Contracts.DTOs;

namespace mk8.email.Application.Interfaces;

public interface IInboxService
{
    Task<InboxDTO?> CreateInboxAsync(Guid userId, CreateInboxRequestDTO request);
    Task<IReadOnlyList<InboxDTO>> GetUserInboxesAsync(Guid userId);
}
