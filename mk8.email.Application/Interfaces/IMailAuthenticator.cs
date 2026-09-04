namespace mk8.email.Application.Interfaces;

public sealed record AuthenticatedMailUser(Guid Id, string Username);

public interface IMailAuthenticator
{
    Task<AuthenticatedMailUser?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
