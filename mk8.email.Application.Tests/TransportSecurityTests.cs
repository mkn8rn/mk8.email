using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TransportSecurityTests
{
    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = CreateCertificate(_testDirectory);
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
        await connection.UpgradeToTlsAsync("mail.mk8n.com");

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
        await connection.UpgradeToTlsAsync("mail.mk8n.com");

        await connection.WriteLineAsync("a2 CAPABILITY");
        var capability = await connection.ReadLineAsync();
        StringAssert.Contains(capability, "AUTH=PLAIN");
        Assert.IsFalse(capability.Contains("LOGINDISABLED", StringComparison.Ordinal));
        Assert.IsFalse(capability.Contains("STARTTLS", StringComparison.Ordinal));
        Assert.IsTrue((await connection.ReadLineAsync()).StartsWith("a2 OK", StringComparison.Ordinal));
    }

    private EnvironmentConfig CreateEnvironment(int? smtpPort = null, int? submissionPort = null, int? imapPort = null)
    {
        return new EnvironmentConfig
        {
            Smtp = new SmtpConfig
            {
                Hostname = "mail.mk8n.com",
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

    private static string CreateCertificate(string directory)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=mail.mk8n.com",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("mail.mk8n.com");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(directory, "server.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return path;
    }

    private sealed class ServerFixture(
        ServiceProvider services,
        IHostedService hostedService,
        StubEmailService emailService) : IAsyncDisposable
    {
        public StubEmailService EmailService { get; } = emailService;

        public static async Task<ServerFixture> StartSmtpAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService) = CreateServices();
            var hostedService = new SmtpServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<SmtpServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService);
            await fixture.StartAsync(port);
            return fixture;
        }

        public static async Task<ServerFixture> StartImapAsync(EnvironmentConfig environment, int port)
        {
            var (services, emailService) = CreateServices();
            var hostedService = new ImapServerService(
                services.GetRequiredService<IServiceScopeFactory>(),
                environment,
                NullLogger<ImapServerService>.Instance);
            var fixture = new ServerFixture(services, hostedService, emailService);
            await fixture.StartAsync(port);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hostedService.StopAsync(timeout.Token);
            await services.DisposeAsync();
        }

        private static (ServiceProvider Services, StubEmailService EmailService) CreateServices()
        {
            var emailService = new StubEmailService();
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<IEmailService>(emailService);
            serviceCollection.AddScoped(_ => new EmailDbContext(new DbContextOptionsBuilder<EmailDbContext>().Options));
            return (serviceCollection.BuildServiceProvider(), emailService);
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

        public string SignWithDkim(string rawMessage, string domain, string selector, string privateKeyPath) => rawMessage;

        public Task<(bool spfPass, bool dkimPass, bool dmarcPass)> VerifyInboundAuthAsync(
            string senderDomain,
            string rawMessage,
            string? clientIp) => Task.FromResult((false, false, false));
    }
}
