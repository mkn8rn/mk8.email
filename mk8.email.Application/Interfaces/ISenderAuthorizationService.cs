namespace mk8.email.Application.Interfaces;

public interface ISenderAuthorizationService
{
    Task<bool> CanSendAsAsync(
        string authenticatedUsername,
        string senderAddress,
        CancellationToken cancellationToken = default);

    bool HasMatchingFromAddress(string rawMessage, string senderAddress);
}
