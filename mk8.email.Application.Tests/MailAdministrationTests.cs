using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class MailAdministrationTests
{
    [TestMethod]
    public async Task CatchAllAcceptsAndDeliversAnUndefinedAddress()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        Assert.IsTrue((await administration.EnsureDomainAsync("mk8n", "mk8n.com")).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin)).Succeeded);
        Assert.IsTrue((await administration.CreateAccountAsync(
            "mk8n@mk8n.com",
            "mailbox-password-value",
            UserRole.User)).Succeeded);
        Assert.IsTrue((await administration.SetCatchAllAsync(
            "mk8n.com",
            "mk8n@mk8n.com")).Succeeded);

        var mail = new EmailService(database, new UnusedRelay());
        Assert.IsTrue(await mail.CanReceiveAsync("undefined@mk8n.com"));
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "undefined@mk8n.com",
            "From: sender@example.net\r\nTo: undefined@mk8n.com\r\nSubject: route test\r\n\r\nbody\r\n"));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "mk8n");
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task ExactAccountTakesPriorityOverCatchAll()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "mk8n.com");
        await administration.CreateAccountAsync(
            "admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin);
        await administration.CreateAccountAsync(
            "mk8n@mk8n.com",
            "mailbox-password-value",
            UserRole.User);
        await administration.SetCatchAllAsync("mk8n.com", "mk8n@mk8n.com");

        var mail = new EmailService(database, new UnusedRelay());
        Assert.IsTrue(await mail.DeliverAsync(
            "sender@example.net",
            "admin@mk8n.com",
            "From: sender@example.net\r\nTo: admin@mk8n.com\r\nSubject: exact test\r\n\r\nbody\r\n"));

        var delivered = await database.Emails.Include(message => message.Folder).SingleAsync();
        var target = await database.Inboxes.SingleAsync(inbox => inbox.Name == "admin");
        Assert.AreEqual(target.Id, delivered.Folder.InboxId);
    }

    [TestMethod]
    public async Task ProvisioningRejectsUnsafeMailboxNames()
    {
        await using var database = CreateDatabase();
        var administration = new MailAdministrationService(database);
        await administration.EnsureDomainAsync("mk8n", "mk8n.com");

        var result = await administration.CreateAccountAsync(
            "../admin@mk8n.com",
            "administrator-password-value",
            UserRole.SuperAdmin);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, await database.Users.CountAsync());
    }

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        database.Database.EnsureCreated();
        return database;
    }

    private sealed class UnusedRelay : IOutboundMailRelay
    {
        public Task<bool> RelayAsync(
            string sender,
            string recipient,
            string rawMessage) =>
            throw new InvalidOperationException("This test does not relay mail.");
    }
}
