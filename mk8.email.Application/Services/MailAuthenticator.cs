using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public sealed class MailAuthenticator(EmailDbContext database) : IMailAuthenticator
{
    private static readonly string DummyPasswordHash =
        PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    public async Task<AuthenticatedMailUser?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalized = SmtpAddress.TryNormalize(username, allowEmpty: false, out var mailbox)
            ? mailbox
            : string.Empty;
        var separator = normalized.LastIndexOf('@');
        var domain = separator > 0 ? normalized[(separator + 1)..] : string.Empty;

        var candidate = await database.Users
            .AsNoTracking()
            .Where(user => user.Username == normalized
                && user.IsActive
                && user.CompanyId != null
                && user.Company != null
                && user.Company.IsActive
                && database.Addresses.Any(address =>
                    address.CompanyId == user.CompanyId
                    && address.Domain == domain
                    && address.IsActive))
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.PasswordHash,
            })
            .SingleOrDefaultAsync(cancellationToken);

        var passwordMatches = PasswordHasher.Verify(
            password,
            candidate?.PasswordHash ?? DummyPasswordHash);
        if (candidate is null || !passwordMatches)
            return null;

        return new AuthenticatedMailUser(candidate.Id, candidate.Username);
    }
}
