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

        var dataLine = new string('a', 1000);
        for (var index = 0; index < 70; index++)
            await connection.WriteLineAsync(dataLine);
        await connection.WriteLineAsync(".");

        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("552 ", StringComparison.Ordinal));
        Assert.AreEqual(0, server.EmailService.DeliverCalls);

        await connection.WriteLineAsync("NOOP");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("250 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpDeliversBoundedMessageAndResetsTransaction()
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
        Assert.AreEqual(1, server.EmailService.DeliverCalls);

        await connection.WriteLineAsync("DATA");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpReturnsTemporaryFailureWhenDkimSigningFails()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port, enableDkimSigning: true);
        await using var server = await ServerFixture.StartSmtpAsync(environment, port);
        server.DkimSigningService.ThrowOnSign = true;
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
        Assert.AreEqual(1, server.DkimSigningService.SignCalls);
        Assert.AreEqual(0, server.EmailService.DeliverCalls);

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
        Assert.AreEqual(1, server.EmailService.DeliverCalls);
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
        Assert.AreEqual(0, server.EmailService.DeliverCalls);
        await connection.WriteLineAsync("RCPT TO:<recipient@example.com>");
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("503 ", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SmtpRejectsMismatchedFromHeaderBeforeSigning()
    {
        var port = ReservePort();
        var environment = CreateEnvironment(smtpPort: port, enableDkimSigning: true);
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
        Assert.AreEqual(0, server.DkimSigningService.SignCalls);
        Assert.AreEqual(0, server.EmailService.DeliverCalls);
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
        var environment = CreateEnvironment(smtpPort: port, enableDkimSigning: true);
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
        Assert.AreEqual(0, server.DkimSigningService.SignCalls);
        Assert.AreEqual(0, server.EmailService.DeliverCalls);
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
        StringAssert.Contains(capability, "LOGINDISABLED");
        StringAssert.Contains(capability, "STARTTLS");
        Assert.IsFalse(capability.Contains("AUTH=PLAIN", StringComparison.Ordinal));
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

    private EnvironmentConfig CreateEnvironment(
        int? smtpPort = null,
        int? submissionPort = null,
        int? imapPort = null,
        bool enableDkimSigning = false)
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
            Dkim = new DkimConfig
            {
                PrivateKeyPath = enableDkimSigning ? "unused-test-key.pem" : null,
                Selector = "default",
                EnableSigning = enableDkimSigning,
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
        StubDkimSigningService dkimSigningService) : IAsyncDisposable
    {
        public StubEmailService EmailService { get; } = emailService;
        public StubDkimSigningService DkimSigningService { get; } = dkimSigningService;

        public static async Task<ServerFixture> StartSmtpAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService, dkimSigningService) = CreateServices();
            var hostedService = new SmtpServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                dkimSigningService,
                NullLogger<SmtpServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, dkimSigningService);
            await fixture.StartAsync(port);
            return fixture;
        }

        public static async Task<ServerFixture> StartImapAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService, dkimSigningService) = CreateServices();
            var hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<ImapServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService, dkimSigningService);
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

        private static (
            ServiceProvider Services,
            StubEmailService EmailService,
            StubDkimSigningService DkimSigningService) CreateServices()
        {
            var emailService = new StubEmailService();
            var dkimSigningService = new StubDkimSigningService();
            var databaseName = $"transport-{Guid.NewGuid():N}";
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddScoped<ISenderAuthorizationService, SenderAuthorizationService>();
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
                database.Inboxes.Add(new InboxDB
                {
                    Id = Guid.CreateVersion7(),
                    Name = "user",
                    Address = address,
                    Owner = user,
                });
                database.SaveChanges();
            }
            return (services, emailService, dkimSigningService);
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
    }

    private sealed class ProtocolConnection : IAsyncDisposable
    {
        private static readonly Encoding ProtocolEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
        public int DeliverCalls { get; private set; }

        public Task<bool> CanReceiveAsync(string recipient) => Task.FromResult(true);

        public Task<bool> DeliverAsync(string sender, string recipient, string rawMessage)
        {
            DeliverCalls++;
            return Task.FromResult(true);
        }

        public Task SaveSentCopyAsync(string sender, string rawMessage) => Task.CompletedTask;

        public Task<bool> RelayAsync(string sender, string recipient, string rawMessage) => Task.FromResult(false);

        public Task<(bool spfPass, bool dkimPass, bool dmarcPass)> VerifyInboundAuthAsync(
            string senderDomain,
            string rawMessage,
            string? clientIp) => Task.FromResult((false, false, false));
    }

    private sealed class StubDkimSigningService : IDkimSigningService
    {
        public bool ThrowOnSign { get; set; }
        public int SignCalls { get; private set; }

        public string Sign(string rawMessage, string domain, string selector, string privateKeyPath)
        {
            SignCalls++;
            if (ThrowOnSign)
                throw new DkimSigningException("Test signing failure.", new InvalidOperationException());
            return rawMessage;
        }
    }
}
