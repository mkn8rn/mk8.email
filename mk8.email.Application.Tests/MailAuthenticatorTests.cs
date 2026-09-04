using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class MailAuthenticatorTests
{
    private const string Username = "user@mk8n.com";
    private const string Password = "test-password-value";

    [TestMethod]
    public async Task ActiveAccountAuthenticatesWithNormalizedUsername()
    {
        await using var database = CreateDatabase(domainActive: true, companyActive: true);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync("USER@MK8N.COM", Password);

        Assert.IsNotNull(result);
        Assert.AreEqual(Username, result.Username);
    }

    [TestMethod]
    public async Task InactiveDomainRejectsMailAuthentication()
    {
        await using var database = CreateDatabase(domainActive: false, companyActive: true);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync(Username, Password);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task InactiveCompanyRejectsMailAuthentication()
    {
        await using var database = CreateDatabase(domainActive: true, companyActive: false);
        var authenticator = new MailAuthenticator(database);

        var result = await authenticator.AuthenticateAsync(Username, Password);

        Assert.IsNull(result);
    }

    private static EmailDbContext CreateDatabase(bool domainActive, bool companyActive)
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"mail-authenticator-{Guid.NewGuid():N}")
            .Options;
        var database = new EmailDbContext(options);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "Test Company",
            IsActive = companyActive,
        };
        database.Addresses.Add(new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "mk8n.com",
            Company = company,
            IsActive = domainActive,
        });
        database.Users.Add(new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = Username,
            PasswordHash = PasswordHasher.Hash(Password),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
