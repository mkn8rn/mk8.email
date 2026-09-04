using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class SmtpServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig env,
    ILogger<SmtpServerService> logger) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 4096;
    private const int MaximumDataLineCharacters = 998;
    private const int MaximumConcurrentConnections = 1000;
    private static readonly Encoding ProtocolEncoding = MailWireEncoding.Instance;

    private enum ListenerMode { Smtp, Submission, ImplicitTls }

    private sealed class SmtpSession
    {
        public required ListenerMode Mode { get; init; }
        public bool IsSecure { get; set; }
        public string? Helo { get; set; }
        public bool HasGreeting => Helo is not null;
        public string? AuthenticatedUser { get; set; }
        public bool IsAuthenticated => AuthenticatedUser is not null;
        public string? Sender { get; set; }
        public bool HasMailFrom { get; set; }
        public List<MailEnvelopeRecipient> Recipients { get; } = [];
        public StringBuilder DataBuilder { get; } = new();
        public int DataByteCount { get; set; }
        public bool MessageTooLarge { get; set; }
        public string? DataFailureResponse { get; set; }
        public bool InDataMode { get; set; }
        public int AuthenticationFailures { get; set; }

        public void Reset()
        {
            Sender = null;
            HasMailFrom = false;
            Recipients.Clear();
            DataBuilder.Clear();
            if (DataBuilder.Capacity > 4096)
                DataBuilder.Capacity = 4096;
            DataByteCount = 0;
            MessageTooLarge = false;
            DataFailureResponse = null;
            InDataMode = false;
        }
    }

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = env.ToGlobalConfig();

        var tasks = new List<Task>();

        if (config.EnableSmtp)
            tasks.Add(ListenAsync(config.SmtpPort, ListenerMode.Smtp, config, stoppingToken));

        if (config.EnableSubmission)
            tasks.Add(ListenAsync(config.SmtpSubmissionPort, ListenerMode.Submission, config, stoppingToken));

        if (config.EnableImplicitTls)
            tasks.Add(ListenAsync(config.SmtpImplicitTlsPort, ListenerMode.ImplicitTls, config, stoppingToken));

        if (tasks.Count == 0)
        {
            logger.LogWarning("No SMTP listeners are enabled.");
            return;
        }

        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port, ListenerMode mode, GlobalConfigDB config, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        logger.LogInformation("SMTP {Mode} listener started on port {Port}", mode, port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, mode, config, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            logger.LogInformation("SMTP {Mode} listener on port {Port} stopped.", mode, port);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, ListenerMode mode, GlobalConfigDB config, CancellationToken ct)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint;
        var remoteIp = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
        var remoteLabel = remoteEndpoint?.ToString() ?? "unknown";

        using var connectionLease = _connectionLimiter.TryAcquire(
            remoteIp,
            config.MaxConnectionsPerIp);
        if (connectionLease is null)
        {
            logger.LogWarning("Rejected SMTP connection from {Endpoint}: connection limit", remoteLabel);
            client.Dispose();
            return;
        }

        try
        {
            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));

                using var scope = scopeFactory.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var submissionQueue = scope.ServiceProvider.GetRequiredService<IMailSubmissionQueue>();
                var senderAuthorization = scope.ServiceProvider.GetRequiredService<ISenderAuthorizationService>();
                var mailAuthenticator = scope.ServiceProvider.GetRequiredService<IMailAuthenticator>();

                Stream stream = client.GetStream();

                if (mode == ListenerMode.ImplicitTls)
                {
                    if (config.TlsCertificatePath is null)
                        throw new InvalidOperationException("Implicit TLS requires a certificate.");

                    using var cert = LoadCertificate(config);
                    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    await AuthenticateAsServerAsync(sslStream, cert, timeout.Token);
                    stream = sslStream;
                }

                using var streamReader = new StreamReader(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var reader = new BoundedLineReader(streamReader);
                await using var writer = new StreamWriter(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n"
                };

                var session = new SmtpSession
                {
                    Mode = mode,
                    IsSecure = mode == ListenerMode.ImplicitTls,
                };
                await writer.WriteLineAsync($"220 {config.SmtpHostname} ESMTP mk8.email");

                await RunSmtpSessionAsync(
                    reader,
                    writer,
                    session,
                    emailService,
                    submissionQueue,
                    senderAuthorization,
                    mailAuthenticator,
                    config,
                    timeout,
                    stream,
                    remoteIp.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("SMTP connection from {Endpoint} timed out", remoteLabel);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error handling SMTP connection from {Endpoint}", remoteLabel);
        }
    }

    private async Task RunSmtpSessionAsync(
        BoundedLineReader reader, StreamWriter writer, SmtpSession session,
        IEmailService emailService, IMailSubmissionQueue submissionQueue,
        ISenderAuthorizationService senderAuthorization,
        IMailAuthenticator mailAuthenticator, GlobalConfigDB config,
        CancellationTokenSource timeout, Stream? upgradableStream = null, string? clientIp = null)
    {

        while (!timeout.IsCancellationRequested)
        {
            var maximumLineLength = session.InDataMode
                ? Math.Min(config.MaxMessageSizeBytes, MaximumDataLineCharacters)
                : MaximumCommandLineCharacters;
            var readResult = await reader.ReadLineAsync(maximumLineLength, timeout.Token);
            if (readResult.Value is null && !readResult.IsTooLong)
                break;

            if (readResult.IsTooLong)
            {
                if (session.InDataMode)
                {
                    session.DataFailureResponse ??= "554 5.6.0 Message line exceeds 998 octets";
                    session.DataBuilder.Clear();
                }
                else
                {
                    await writer.WriteLineAsync("500 5.5.2 Line too long");
                }

                continue;
            }

            var line = readResult.Value!;

            if (session.InDataMode)
            {
                if (line == ".")
                {
                    session.InDataMode = false;
                    if (session.MessageTooLarge)
                    {
                        await writer.WriteLineAsync("552 5.3.4 Message exceeds server limits");
                        session.Reset();
                    }
                    else if (session.DataFailureResponse is not null)
                    {
                        await writer.WriteLineAsync(session.DataFailureResponse);
                        session.Reset();
                    }
                    else
                    {
                        var raw = session.DataBuilder.ToString();

                        if (session.IsAuthenticated
                            && (!await senderAuthorization.CanSendAsAsync(
                                    session.AuthenticatedUser!, session.Sender ?? string.Empty, timeout.Token)
                                || !senderAuthorization.HasMatchingFromAddress(
                                    raw,
                                    session.Sender ?? string.Empty)))
                        {
                            await writer.WriteLineAsync("550 5.7.1 Sender identity is not authorized");
                            session.Reset();
                            continue;
                        }

                        var queueId = Guid.CreateVersion7();
                        var receivedMessage = BuildReceivedHeader(
                            queueId,
                            config.SmtpHostname,
                            session.Helo,
                            clientIp,
                            session.IsSecure,
                            session.IsAuthenticated) + raw;
                        try
                        {
                            await submissionQueue.EnqueueAsync(
                                new MailSubmission(
                                    queueId,
                                    session.Sender ?? string.Empty,
                                    session.Recipients.ToList(),
                                    receivedMessage,
                                    clientIp,
                                    session.Helo,
                                    session.AuthenticatedUser),
                                timeout.Token);
                        }
                        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            logger.LogError(exception, "Could not persist SMTP queue message {QueueId}", queueId);
                            await writer.WriteLineAsync("451 4.3.0 Queue storage is temporarily unavailable");
                            session.Reset();
                            continue;
                        }

                        logger.LogInformation(
                            "Accepted SMTP queue message {QueueId} with {RecipientCount} recipients",
                            queueId,
                            session.Recipients.Count);
                        await writer.WriteLineAsync($"250 2.0.0 Queued as {queueId:N}");
                        session.Reset();
                    }
                }
                else
                {
                    var messageLine = line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line;
                    var lineByteCount = MailWireEncoding.Instance.GetByteCount(messageLine) + 2;

                    if (messageLine.Contains('\0'))
                    {
                        session.DataFailureResponse ??= "554 5.6.0 NUL bytes are not supported";
                        session.DataBuilder.Clear();
                        continue;
                    }

                    if (!session.MessageTooLarge && session.DataFailureResponse is null)
                    {
                        if (lineByteCount > config.MaxMessageSizeBytes - session.DataByteCount)
                        {
                            session.MessageTooLarge = true;
                            session.DataBuilder.Clear();
                        }
                        else
                        {
                            session.DataBuilder.Append(messageLine).Append("\r\n");
                            session.DataByteCount += lineByteCount;
                        }
                    }
                }
                continue;
            }

            var spaceIdx = line.IndexOf(' ');
            var verb = (spaceIdx > 0 ? line[..spaceIdx] : line).ToUpperInvariant();

            switch (verb)
            {
                case "EHLO":
                    if (!TryGetGreeting(line, out var ehlo))
                    {
                        await writer.WriteLineAsync("501 5.5.4 A valid EHLO argument is required");
                        break;
                    }
                    session.Reset();
                    session.Helo = ehlo;
                    await WriteEhloAsync(writer, config, session.IsSecure);
                    break;

                case "HELO":
                    if (!TryGetGreeting(line, out var helo))
                    {
                        await writer.WriteLineAsync("501 5.5.4 A valid HELO argument is required");
                        break;
                    }
                    session.Reset();
                    session.Helo = helo;
                    await writer.WriteLineAsync($"250 {config.SmtpHostname}");
                    break;

                case "AUTH":
                    if (!session.HasGreeting)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Send EHLO first");
                        break;
                    }
                    if (!session.IsSecure)
                    {
                        await writer.WriteLineAsync("538 5.7.11 Encryption required for authentication");
                        break;
                    }
                    if (session.Sender is not null)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Mail transaction is already active");
                        break;
                    }
                    await HandleAuthAsync(
                        line,
                        reader,
                        writer,
                        session,
                        mailAuthenticator,
                        clientIp ?? "unknown",
                        timeout.Token);
                    if (session.AuthenticationFailures >= 5)
                    {
                        await writer.WriteLineAsync("421 4.7.0 Too many authentication failures");
                        return;
                    }
                    break;

                case "MAIL":
                    if (!session.HasGreeting)
                    {
                        await writer.WriteLineAsync("503 5.5.1 Send EHLO or HELO first");
                        break;
                    }
                    if (!session.IsSecure && (session.Mode == ListenerMode.Submission || config.RequireTls))
                    {
                        await writer.WriteLineAsync("530 5.7.0 Issue STARTTLS first");
                        break;
                    }
                    if (session.Mode is ListenerMode.Submission && !session.IsAuthenticated && config.RequireAuth)
                    {
                        await writer.WriteLineAsync("530 5.7.0 Authentication required");
                        break;
                    }
                    session.Reset();
                    if (!TryExtractPath(line, "FROM", allowEmpty: !session.IsAuthenticated, out var sender))
                    {
                        await writer.WriteLineAsync("501 5.1.7 Sender address syntax is invalid");
                        break;
                    }
                    if (session.IsAuthenticated
                        && !await senderAuthorization.CanSendAsAsync(
                            session.AuthenticatedUser!, sender, timeout.Token))
                    {
                        await writer.WriteLineAsync("553 5.7.1 Sender address is not authorized");
                        break;
                    }
                    session.Sender = sender;
                    session.HasMailFrom = true;
                    await writer.WriteLineAsync("250 2.1.0 OK");
                    break;

                case "RCPT":
                    if (!session.HasMailFrom)
                    {
                        await writer.WriteLineAsync("503 5.5.1 MAIL FROM required first");
                        break;
                    }
                    if (session.Recipients.Count >= config.MaxRecipientsPerMessage)
                    {
                        await writer.WriteLineAsync("452 4.5.3 Too many recipients");
                        break;
                    }
                    if (!TryExtractPath(line, "TO", allowEmpty: false, out var rcpt))
                    {
                        await writer.WriteLineAsync("501 5.1.3 Recipient address syntax is invalid");
                        break;
                    }
                    var isLocal = await emailService.CanReceiveAsync(rcpt, timeout.Token);
                    if (isLocal)
                    {
                        AddRecipient(session, rcpt, isLocal: true);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                    }
                    else if (session.IsAuthenticated && config.AllowRelay)
                    {
                        AddRecipient(session, rcpt, isLocal: false);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 5.1.1 No such user");
                    }
                    break;

                case "DATA":
                    if (session.Recipients.Count == 0)
                    {
                        await writer.WriteLineAsync("503 5.5.1 No valid recipients");
                    }
                    else
                    {
                        session.InDataMode = true;
                        await writer.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>");
                    }
                    break;

                case "STARTTLS":
                    if (session.IsSecure)
                    {
                        await writer.WriteLineAsync("503 5.5.1 TLS is already active");
                    }
                    else if (config.EnableStartTls && config.TlsCertificatePath is not null
                        && upgradableStream is not null)
                    {
                        await writer.WriteLineAsync("220 Ready to start TLS");
                        await writer.FlushAsync(timeout.Token);

                        using var cert = LoadCertificate(config);
                        var tlsStream = new SslStream(upgradableStream, leaveInnerStreamOpen: false);
                        await AuthenticateAsServerAsync(tlsStream, cert, timeout.Token);

                        var tlsStreamReader = new StreamReader(tlsStream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                        var tlsReader = new BoundedLineReader(tlsStreamReader);
                        var tlsWriter = new StreamWriter(tlsStream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
                        {
                            AutoFlush = true,
                            NewLine = "\r\n"
                        };

                        session.Reset();
                        session.AuthenticatedUser = null;
                        session.Helo = null;
                        session.IsSecure = true;

                        await RunSmtpSessionAsync(
                            tlsReader,
                            tlsWriter,
                            session,
                            emailService,
                            submissionQueue,
                            senderAuthorization,
                            mailAuthenticator,
                            config,
                            timeout,
                            clientIp: clientIp);
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("502 STARTTLS not enabled");
                    }
                    break;

                case "RSET":
                    session.Reset();
                    await writer.WriteLineAsync("250 2.0.0 OK");
                    break;

                case "NOOP":
                    await writer.WriteLineAsync("250 2.0.0 OK");
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    return;

                case "VRFY":
                    await writer.WriteLineAsync("252 2.5.2 Cannot VRFY user, but will accept message");
                    break;

                case "EXPN":
                    await writer.WriteLineAsync("252 2.5.2 Cannot supply mailing list info");
                    break;

                default:
                    await writer.WriteLineAsync("502 5.5.1 Command not implemented");
                    break;
            }
        }
    }

    private static async Task WriteEhloAsync(StreamWriter writer, GlobalConfigDB config, bool isSecure)
    {
        await writer.WriteLineAsync($"250-{config.SmtpHostname}");
        await writer.WriteLineAsync($"250-SIZE {config.MaxMessageSizeBytes}");
        await writer.WriteLineAsync("250-8BITMIME");
        await writer.WriteLineAsync("250-PIPELINING");
        await writer.WriteLineAsync("250-ENHANCEDSTATUSCODES");
        if (config.EnableStartTls && !isSecure)
            await writer.WriteLineAsync("250-STARTTLS");
        if (isSecure)
            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
        await writer.WriteLineAsync("250 OK");
    }

    private static X509Certificate2 LoadCertificate(GlobalConfigDB config)
    {
        if (config.TlsCertificateKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(config.TlsCertificatePath!, config.TlsCertificateKeyPath);
        return X509CertificateLoader.LoadPkcs12FromFile(config.TlsCertificatePath!, password: null);
    }

    private static Task AuthenticateAsServerAsync(
        SslStream stream,
        X509Certificate2 certificate,
        CancellationToken cancellationToken) =>
        stream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            cancellationToken);

    private async Task HandleAuthAsync(
        string line, BoundedLineReader reader, StreamWriter writer,
        SmtpSession session, IMailAuthenticator mailAuthenticator,
        string clientIp, CancellationToken ct)
    {
        if (session.IsAuthenticated)
        {
            await writer.WriteLineAsync("503 5.5.1 Already authenticated");
            return;
        }

        var parts = line.Split(' ', 3);
        if (parts.Length < 2)
        {
            await writer.WriteLineAsync("501 5.5.4 Syntax error");
            return;
        }

        var mechanism = parts[1].ToUpperInvariant();

        string? username = null;
        string? password = null;

        switch (mechanism)
        {
            case "PLAIN":
                {
                    var encoded = parts.Length == 3 ? parts[2] : null;
                    if (encoded is null)
                    {
                        await writer.WriteLineAsync("334 ");
                        var encodedResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
                        encoded = encodedResult.Value;
                        if (encodedResult.IsTooLong)
                        {
                            await writer.WriteLineAsync("501 Authentication response is too long");
                            return;
                        }
                        if (encoded is null || encoded == "*")
                        {
                            await writer.WriteLineAsync("501 Authentication cancelled");
                            return;
                        }
                    }

                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                        var fields = decoded.Split('\0');
                        if (fields.Length >= 3)
                        {
                            username = string.IsNullOrEmpty(fields[0]) ? fields[1] : fields[0];
                            password = fields[2];
                        }
                    }
                    catch (FormatException)
                    {
                        await writer.WriteLineAsync("501 Invalid base64");
                        return;
                    }
                    break;
                }

            case "LOGIN":
                {
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                    var userResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
                    var userB64 = userResult.Value;
                    if (userResult.IsTooLong) { await writer.WriteLineAsync("501 Authentication response is too long"); return; }
                    if (userB64 is null or "*") { await writer.WriteLineAsync("501 Authentication cancelled"); return; }

                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                    var passwordResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
                    var passB64 = passwordResult.Value;
                    if (passwordResult.IsTooLong) { await writer.WriteLineAsync("501 Authentication response is too long"); return; }
                    if (passB64 is null or "*") { await writer.WriteLineAsync("501 Authentication cancelled"); return; }

                    try
                    {
                        username = Encoding.UTF8.GetString(Convert.FromBase64String(userB64));
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(passB64));
                    }
                    catch (FormatException)
                    {
                        await writer.WriteLineAsync("501 Invalid base64");
                        return;
                    }
                    break;
                }

            default:
                await writer.WriteLineAsync("504 Unsupported authentication mechanism");
                return;
        }

        if (username is null || password is null)
        {
            RecordAuthenticationFailure(session, clientIp);
            await writer.WriteLineAsync("535 5.7.8 Authentication failed");
            return;
        }

        var user = await mailAuthenticator.AuthenticateAsync(username, password, ct);
        if (user is null)
        {
            RecordAuthenticationFailure(session, clientIp);
            await writer.WriteLineAsync("535 5.7.8 Authentication failed");
            return;
        }

        session.AuthenticatedUser = user.Username;
        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
    }

    private void RecordAuthenticationFailure(SmtpSession session, string clientIp)
    {
        session.AuthenticationFailures++;
        logger.LogWarning(
            "Mail authentication failed for protocol SMTP from {RemoteIp}",
            clientIp);
    }

    private static void AddRecipient(SmtpSession session, string recipient, bool isLocal)
    {
        if (session.Recipients.Any(item =>
                string.Equals(item.Address, recipient, StringComparison.OrdinalIgnoreCase)))
            return;

        session.Recipients.Add(new MailEnvelopeRecipient(recipient, isLocal));
    }

    private static bool TryExtractPath(
        string line,
        string pathName,
        bool allowEmpty,
        out string address)
    {
        address = string.Empty;
        var colon = line.IndexOf(':');
        if (colon < 0)
            return false;

        var prefix = line[..colon].Trim();
        if (!prefix.Equals($"{(pathName == "FROM" ? "MAIL" : "RCPT")} {pathName}", StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line[(colon + 1)..].TrimStart();
        string candidate;
        if (remainder.StartsWith('<'))
        {
            var close = remainder.IndexOf('>');
            if (close < 0)
                return false;
            candidate = remainder[1..close];
            remainder = remainder[(close + 1)..];
        }
        else
        {
            var separator = remainder.IndexOf(' ');
            candidate = separator < 0 ? remainder : remainder[..separator];
            remainder = separator < 0 ? string.Empty : remainder[separator..];
        }

        if (!string.IsNullOrWhiteSpace(remainder)
            && remainder.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(parameter => !IsSafeEsmtpParameter(parameter)))
        {
            return false;
        }

        return SmtpAddress.TryNormalize(candidate, allowEmpty, out address);
    }

    private static bool IsSafeEsmtpParameter(string value) =>
        value.Length is > 0 and <= 256
        && value.All(char.IsAscii)
        && !value.ContainsAny(['\r', '\n', '\0', '<', '>']);

    private static bool TryGetGreeting(string line, out string greeting)
    {
        greeting = string.Empty;
        var separator = line.IndexOf(' ');
        if (separator < 0)
            return false;

        var value = line[(separator + 1)..].Trim();
        if (value.Length is 0 or > 255
            || !value.All(char.IsAscii)
            || value.ContainsAny(['\r', '\n', '\0', ' ', '\t']))
        {
            return false;
        }

        greeting = value;
        return true;
    }

    private static string BuildReceivedHeader(
        Guid queueId,
        string host,
        string? helo,
        string? clientIp,
        bool isSecure,
        bool isAuthenticated)
    {
        var protocol = isSecure ? "ESMTPS" : "ESMTP";
        if (isAuthenticated)
            protocol += "A";

        var source = string.IsNullOrWhiteSpace(helo) ? "unknown" : helo;
        var address = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        return $"Received: from {source} ([{address}])\r\n" +
               $"\tby {host} with {protocol} id {queueId:N}; {DateTimeOffset.UtcNow:r}\r\n";
    }
}
