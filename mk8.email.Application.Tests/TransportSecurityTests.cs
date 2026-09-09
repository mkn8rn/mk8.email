using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TransportSecurityTests
{
    private const string TestUsername = "user@mk8n.com";
    private const string TestPassword = "correct horse battery staple";

    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = TestCertificateFactory.Create(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpClearTextRejectsAuthenticationAndLongCommands()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("EHLO client.example");
        var capability = await connection.ReadSmtpResponseAsync();
        StringAssert.Contains(capability, "250-STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH", StringComparison.Ordinal));
        StringAssert.Contains(capability, "250-8BITMIME");

        await connection.WriteLineAsync("AUTH PLAIN AGZvbwBiYXI=");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("538 ", StringComparison.Ordinal));

        await connection.WriteLineAsync(new string('X', 5000));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("500 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpStartTlsAdvertisesAuthenticationOnlyAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        Assert.IsFalse((await connection.ReadSmtpResponseAsync()).Contains("AUTH", StringComparison.Ordinal));

        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync("EHLO client.example");
        var capability = await connection.ReadSmtpResponseAsync();
        StringAssert.Contains(capability, "250-AUTH PLAIN LOGIN");
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpSubmissionRequiresTlsBeforeMail()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(submissionPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@mk8n.com>");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("530 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMessageWhenBufferedDataReachesItsLimit()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));

        var dataLine = new string('a', 900);
        for (var index = 0; index < 80; index++)
            await connection.WriteLineAsync(dataLine);
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("552 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SmtpBoundsConcurrentDataBuffers()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 5; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port);
                connections.Add(connection);
                await connection.ReadLineAsync();
                await BeginInboundEnvelopeAsync(connection);
                await connection.WriteLineAsync("DATA");

                var response = await connection.ReadLineAsync();
                if (index < 4)
                    Assert.IsTrue(response.StartsWith("354 ", StringComparison.Ordinal));
                else
                    Assert.IsTrue(response.StartsWith("452 4.3.2", StringComparison.Ordinal));
            }

            await connections[0].WriteLineAsync(".");
            Assert.IsTrue((await connections[0].ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));

            await connections[4].WriteLineAsync("DATA");
            Assert.IsTrue((await connections[4].ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsLongDataAndPreservesEightBitData()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await BeginInboundMessageAsync(connection);
        await connection.WriteLineAsync(new string('a', 999));
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("554 5.6.0", StringComparison.Ordinal));

        await BeginInboundMessageAsync(connection);
        await connection.WriteLineAsync("Subject: café");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        StringAssert.Contains(server.MailQueue.LastSubmission!.RawMessage, "café");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpQueuesBoundedMessageAndResetsTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("Subject: bounded");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("message body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
        Assert.IsNotNull(server.MailQueue.LastSubmission);
        StringAssert.StartsWith(server.MailQueue.LastSubmission.RawMessage, "Received: from client.example");
        Assert.AreEqual("sender@example.com", server.MailQueue.LastSubmission.EnvelopeSender);
        Assert.AreEqual("postmaster@mk8n.com", server.MailQueue.LastSubmission.Recipients.Single().Address);
        Assert.IsTrue(server.MailQueue.LastSubmission.Recipients.Single().IsLocal);

        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpReturnsTemporaryFailureWhenQueuePersistenceFails()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        server.MailQueue.ThrowOnEnqueue = true;
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync($"From: {TestUsername}");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: signing failure");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("451 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpAcceptsOwnedSenderWithMatchingFromHeader()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"From: Test User <{TestUsername}>");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: authorized sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        Assert.AreEqual(1, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsUnownedEnvelopeSender()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync("MAIL FROM:<other@mk8n.com>");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("553 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMismatchedFromHeaderBeforeSigning()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("From: other@mk8n.com");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: rejected sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsAuthenticationDuringMailTransaction()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await UpgradeSmtpToTlsAsync(connection);
        await connection.WriteLineAsync("MAIL FROM:<other@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RSET");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("235 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRechecksSenderOwnershipBeforeDelivery()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await AuthenticateSmtpAsync(connection);
        await connection.WriteLineAsync($"MAIL FROM:<{TestUsername}>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await server.DisableOwnedAddressAsync();
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("DATA");
        await connection.ReadLineAsync();
        await connection.WriteLineAsync($"From: {TestUsername}");
        await connection.WriteLineAsync("To: recipient@example.com");
        await connection.WriteLineAsync("Subject: disabled sender");
        await connection.WriteLineAsync(string.Empty);
        await connection.WriteLineAsync("body");
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("550 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.MailQueue.EnqueueCalls);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapClearTextDisablesLoginAndHasNoByteOrderMark()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("* OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a1 CAPABILITY");
        var capability = await connection.ReadLineAsync();
        StringAssert.Contains(capability, "IMAP4rev1 LITERAL+ IDLE NAMESPACE SPECIAL-USE UIDPLUS");
        StringAssert.Contains(capability, "LOGINDISABLED");
        StringAssert.Contains(capability, "STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH=PLAIN", StringComparison.Ordinal));
        foreach (var unverifiedExtension in new[]
                 {
                     "CONDSTORE", "QRESYNC", "ESEARCH", "MULTIAPPEND",
                     "COMPRESS=DEFLATE", "BINARY", "OBJECTID", "SORT", "THREAD=REFERENCES",
                 })
        {
            Assert.IsFalse(
                capability.Contains(unverifiedExtension, StringComparison.Ordinal),
                $"The server advertised the unverified {unverifiedExtension} extension.");
        }
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a2 LOGIN user password");
        StringAssert.Contains(await connection.ReadLineAsync(), "[PRIVACYREQUIRED]");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapStartTlsAdvertisesAuthenticationAfterUpgrade()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync("a2 CAPABILITY");
        var capability = await connection.ReadLineAsync();
        StringAssert.Contains(capability, "AUTH=PLAIN");
        Assert.IsFalse(capability.Contains("LOGINDISABLED", StringComparison.Ordinal));
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapPrimaryMailboxUsesStandardInboxName()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");

        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 LIST \"\" \"*\"");
        var listLines = new List<string>();
        string line;
        do
        {
            line = await connection.ReadLineAsync();
            listLines.Add(line);
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        Assert.IsTrue(listLines.Any(value => value.Contains("\"INBOX\"", StringComparison.Ordinal)));

        await connection.WriteLineAsync("a4 SELECT INBOX");
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(line.StartsWith("a4 OK [READ-WRITE]", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSearchMatchesOnlyTheRequestedHeaderValue()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedInboxMessagesForSearchAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
        await connection.WriteLineAsync("a3 SELECT INBOX");

        string response;
        do
        {
            response = await connection.ReadLineAsync();
        }
        while (!response.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 UID SEARCH HEADER X-Mk8-Test mixedmarker42");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a5 UID SEARCH SUBJECT mixedcasesubject");
        Assert.AreEqual("* SEARCH 1", await connection.ReadLineAsync());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a5 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a6 UID SEARCH HEADER X-Mk8-Test absent-marker");
        Assert.AreEqual("* SEARCH", (await connection.ReadLineAsync()).TrimEnd());
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a6 OK", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendPreservesLiteralOctetsAndLeadingBodyLines()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: café\r\n" +
            "\r\n" +
            "\r\nbody é\r\n";
        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));

        var stored = await server.GetStoredEmailAsync(DefaultFolders.Sent);
        Assert.AreEqual("café", stored.Subject);
        Assert.AreEqual("\r\nbody é\r\n", stored.Body);
        Assert.AreEqual(message.Length, stored.SizeBytes);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapAppendChecksQuotaBeforeReadingTheLiteral()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: quota\r\n" +
            "\r\n" +
            "body\r\n";
        await server.SetUserQuotaAsync(message.Length - 1);
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a3 NO [OVERQUOTA]", StringComparison.Ordinal));
        Assert.AreEqual(0, await server.CountStoredEmailsAsync());

        await server.SetUserQuotaAsync(message.Length);
        await connection.WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        await connection.WriteRawAsync(message);
        await connection.WriteLineAsync(string.Empty);
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a4 OK [APPENDUID", StringComparison.Ordinal));
        Assert.AreEqual(1, await server.CountStoredEmailsAsync());
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ImapBoundsConcurrentAppendBuffers()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        const string message =
            "From: user@mk8n.com\r\n" +
            "To: user@mk8n.com\r\n" +
            "Subject: bounded append\r\n\r\n" +
            "body\r\n";
        var connections = new List<ProtocolConnection>();

        try
        {
            for (var index = 0; index < 3; index++)
            {
                var connection = await ProtocolConnection.ConnectAsync(port);
                connections.Add(connection);
                await connection.ReadLineAsync();
                await connection.WriteLineAsync("a1 STARTTLS");
                Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
                await connection.UpgradeToTlsAsync("email.mk8n.com");
                await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
                Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
            }

            for (var index = 0; index < 2; index++)
            {
                await connections[index].WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
                Assert.IsTrue((await connections[index].ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
            }

            await connections[2].WriteLineAsync($"a3 APPEND \"Sent\" {{{message.Length}}}");
            Assert.IsTrue((await connections[2].ReadLineAsync()).StartsWith("a3 NO [UNAVAILABLE]", StringComparison.Ordinal));

            await connections[0].WriteRawAsync(message);
            await connections[0].WriteLineAsync(string.Empty);
            Assert.IsTrue((await connections[0].ReadLineAsync()).StartsWith("a3 OK [APPENDUID", StringComparison.Ordinal));

            await connections[2].WriteLineAsync($"a4 APPEND \"Sent\" {{{message.Length}}}");
            Assert.IsTrue((await connections[2].ReadLineAsync()).StartsWith("+ ", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ImapSequenceNumbersFollowUidOrder()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(imapPort: port);
        await using var server = await ServerFixture.StartImapAsync(environment, port);
        await server.SeedSentMessagesWithReverseDatesAsync();
        await using var connection = await ProtocolConnection.ConnectAsync(port);

        await connection.ReadLineAsync();
        await connection.WriteLineAsync("a1 STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a1 OK", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync($"a2 LOGIN \"{TestUsername}\" \"{TestPassword}\"");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));

        await connection.WriteLineAsync("a3 SELECT Sent");
        string line;
        do
        {
            line = await connection.ReadLineAsync();
        }
        while (!line.StartsWith("a3 ", StringComparison.Ordinal));

        await connection.WriteLineAsync("a4 FETCH 1:* (UID)");
        var responses = new List<string>();
        do
        {
            line = await connection.ReadLineAsync();
            responses.Add(line);
        }
        while (!line.StartsWith("a4 ", StringComparison.Ordinal));

        Assert.IsTrue(responses[0].StartsWith("* 1 FETCH (UID 1)", StringComparison.Ordinal));
        Assert.IsTrue(responses[1].StartsWith("* 2 FETCH (UID 2)", StringComparison.Ordinal));
        Assert.IsTrue(responses[^1].StartsWith("a4 OK", StringComparison.Ordinal));
    }

    private EnvironmentConfig CreateEnvironment(
        int? smtpPort = null,
        int? submissionPort = null,
        int? imapPort = null)
    {
        return new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "email.mk8n.com",
                Port = smtpPort ?? ReservePort(),
                SubmissionPort = submissionPort ?? ReservePort(),
                ImplicitTlsPort = ReservePort(),
                EnableSmtp = smtpPort.HasValue,
                EnableSubmission = submissionPort.HasValue,
                EnableImplicitTls = false,
                EnableStartTls = true,
                RequireAuth = true,
                AllowRelay = true,
            },
            Imap = new ImapConfig
            {
                Port = imapPort ?? ReservePort(),
                ImplicitTlsPort = ReservePort(),
                EnableImap = imapPort.HasValue,
                EnableImplicitTls = false,
            },
            Tls = new TlsConfig
            {
                CertificatePath = _certificatePath,
            },
            Limits = new LimitsConfig
            {
                MaxMessageSizeBytes = 65_536,
                MaxRecipientsPerMessage = 100,
                ConnectionTimeoutSeconds = 10,
                MaxConnectionsPerIp = 10,
            },
        };
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task AuthenticateSmtpAsync(ProtocolConnection connection)
    {
        await UpgradeSmtpToTlsAsync(connection);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{TestUsername}\0{TestPassword}"));
        await connection.WriteLineAsync($"AUTH PLAIN {credentials}");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("235 ", StringComparison.Ordinal));
    }

    private static async Task BeginInboundMessageAsync(ProtocolConnection connection)
    {
        await BeginInboundEnvelopeAsync(connection);
        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("354 ", StringComparison.Ordinal));
    }

    private static async Task BeginInboundEnvelopeAsync(ProtocolConnection connection)
    {
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("MAIL FROM:<sender@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
        await connection.WriteLineAsync("RCPT TO:<postmaster@mk8n.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    private static async Task UpgradeSmtpToTlsAsync(ProtocolConnection connection)
    {
        await connection.ReadLineAsync();
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
        await connection.WriteLineAsync("STARTTLS");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("220 ", StringComparison.Ordinal));
        await connection.UpgradeToTlsAsync("email.mk8n.com");
        await connection.WriteLineAsync("EHLO client.example");
        await connection.ReadSmtpResponseAsync();
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService,
        StubEmailService emailService,
        StubMailSubmissionQueue mailQueue) : IAsyncDisposable
    {
        public StubEmailService EmailService { get; } = emailService;
        public StubMailSubmissionQueue MailQueue { get; } = mailQueue;

        public static async Task<ServerFixture> StartSmtpAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService, mailQueue) = CreateServices();
            var hostedService = new SmtpServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<SmtpServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, mailQueue);
            await fixture.StartAsync(port);
            return fixture;
        }

        public static async Task<ServerFixture> StartImapAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService, mailQueue) = CreateServices();
            var hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<ImapServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, mailQueue);
            await fixture.StartAsync(port);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hostedService.StopAsync(timeout.Token);
            await services.DisposeAsync();
        }

        public async Task DisableOwnedAddressAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var address = await database.Addresses.SingleAsync(item => item.Domain == "mk8n.com");
            address.IsActive = false;
            await database.SaveChangesAsync();
        }

        public async Task SetUserQuotaAsync(long quotaBytes)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var user = await database.Users.SingleAsync(item => item.Username == TestUsername);
            user.QuotaBytes = quotaBytes;
            await database.SaveChangesAsync();
        }

        public async Task<int> CountStoredEmailsAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails.CountAsync();
        }

        private static (
            ServiceProvider Services,
            StubEmailService EmailService,
            StubMailSubmissionQueue MailQueue) CreateServices()
        {
            var emailService = new StubEmailService();
            var mailQueue = new StubMailSubmissionQueue();
            var databaseName = $"transport-{Guid.NewGuid():N}";
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddSingleton<IMailSubmissionQueue>(mailQueue);
            serviceCollection.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
            serviceCollection.AddScoped<IMailAuthenticator, MailAuthenticator>();
            serviceCollection.AddDbContext<EmailDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            var services = serviceCollection.BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                var company = new CompanyDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "Test Company",
                    IsActive = true,
                };
                var address = new AddressDB
                {
                    Id = Guid.CreateVersion7(),
                    Domain = "mk8n.com",
                    IsActive = true,
                    Company = company,
                };
                var user = new UserDB
                {
                    Id = Guid.CreateVersion7(),
                    Username = TestUsername,
                    PasswordHash = PasswordHasher.Hash(TestPassword),
                    Role = nameof(UserRole.User),
                    IsActive = true,
                    Company = company,
                };
                var inbox = new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "user",
                    Address = address,
                    Owner = user,
                };
                database.Inboxes.Add(inbox);
                database.Folders.Add(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = DefaultFolders.Inbox,
                    Inbox = inbox,
                });
                database.Folders.Add(new FolderDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = DefaultFolders.Sent,
                    Inbox = inbox,
                });
                database.SaveChanges();
            }
            return (services, emailService, mailQueue);
        }

        private async Task StartAsync(int port)
        {
            await hostedService.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }

            throw new TimeoutException($"The test server did not listen on port {port}.");
        }

        public async Task<EmailDB> GetStoredEmailAsync(string folderName)
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            return await database.Emails
                .AsNoTracking()
                .Include(email => email.Folder)
                .SingleAsync(email => email.Folder.Name == folderName);
        }

        public async Task SeedSentMessagesWithReverseDatesAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Sent);
            database.Emails.AddRange(
                CreateStoredEmail(folder.Id, uid: 1, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
                CreateStoredEmail(folder.Id, uid: 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            folder.NextUid = 3;
            await database.SaveChangesAsync();
        }

        public async Task SeedInboxMessagesForSearchAsync()
        {
            using var scope = services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
            var folder = await database.Folders.SingleAsync(item => item.Name == DefaultFolders.Inbox);
            database.Emails.AddRange(
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "MixedCaseSubject",
                    Body = "first body\r\n",
                    RawHeaders =
                        "From: sender@example.net\r\n" +
                        $"To: {TestUsername}\r\n" +
                        "Subject: MixedCaseSubject\r\n" +
                        "X-Mk8-Test: prefix\r\n\tMiXeDMarker42",
                    SizeBytes = 180,
                    Uid = 1,
                    ModSeq = 1,
                    FolderId = folder.Id,
                    ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new EmailDB
                {
                    Id = Guid.CreateVersion7(),
                    Sender = "sender@example.net",
                    Recipient = TestUsername,
                    Subject = "Other subject",
                    Body = "second body\r\n",
                    RawHeaders =
                        "From: sender@example.net\r\n" +
                        $"To: {TestUsername}\r\n" +
                        "Subject: Other subject\r\n" +
                        "X-Mk8-Test: another value\r\n" +
                        "X-Unrelated: MiXeDMarker42",
                    SizeBytes = 190,
                    Uid = 2,
                    ModSeq = 2,
                    FolderId = folder.Id,
                    ReceivedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                });
            folder.NextUid = 3;
            await database.SaveChangesAsync();
        }

        private static EmailDB CreateStoredEmail(Guid folderId, int uid, DateTime receivedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            Sender = "sender@example.net",
            Recipient = TestUsername,
            Subject = $"UID {uid}",
            Body = "body\r\n",
            RawHeaders = $"From: sender@example.net\r\nTo: {TestUsername}\r\nSubject: UID {uid}",
            SizeBytes = 100,
            Uid = uid,
            ModSeq = uid,
            FolderId = folderId,
            ReceivedAt = receivedAt,
        };
    }

    private sealed class ProtocolConnection : IAsyncDisposable
    {
        private static readonly Encoding ProtocolEncoding = Encoding.Latin1;
        private readonly TcpClient _client;
        private Stream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;

        private ProtocolConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
        }

        public static async Task<ProtocolConnection> ConnectAsync(int port)
        {
            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return new ProtocolConnection(client);
        }

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _reader.ReadLineAsync(timeout.Token)
                ?? throw new EndOfStreamException("The server closed the protocol stream.");
        }

        public async Task<string> ReadSmtpResponseAsync()
        {
            var response = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync();
                if (response.Length > 0)
                    response.Append('\n');
                response.Append(line);

                if (line.Length >= 4 && line[3] != '-')
                    return response.ToString();
            }
        }

        public Task WriteLineAsync(string line) => _writer.WriteLineAsync(line);

        public async Task WriteRawAsync(string value)
        {
            await _writer.WriteAsync(value);
            await _writer.FlushAsync();
        }

        public async Task UpgradeToTlsAsync(string hostName)
        {
            await _writer.FlushAsync();
            _reader.Dispose();
            await _writer.DisposeAsync();

            var tlsStream = new SslStream(
                _stream,
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = hostName },
                timeout.Token);

            _stream = tlsStream;
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
            _client.Dispose();
        }

        private static StreamReader CreateReader(Stream stream) =>
            new(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        private static StreamWriter CreateWriter(Stream stream) =>
            new(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };
    }

    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> CanReceiveAsync(
            string recipient,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeliverAsync(
            string sender,
            string recipient,
            string rawMessage,
            string folderName = DefaultFolders.Inbox,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMTP listener must use the durable queue.");

        public Task<bool> SaveSentCopyAsync(
            string sender,
            string rawMessage,
            Guid? queueDeliveryId = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The SMTP listener must use the durable queue.");
    }

    private sealed class StubMailSubmissionQueue : IMailSubmissionQueue
    {
        public bool ThrowOnEnqueue { get; set; }
        public int EnqueueCalls { get; private set; }
        public MailSubmission? LastSubmission { get; private set; }

        public Task<Guid> EnqueueAsync(
            MailSubmission submission,
            CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            LastSubmission = submission;
            if (ThrowOnEnqueue)
                throw new IOException("Test queue failure.");
            return Task.FromResult(submission.QueueId);
        }
    }
}
