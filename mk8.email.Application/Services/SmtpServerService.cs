using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public class SmtpServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig env,
    IDkimSigningService dkimSigningService,
    ILogger<SmtpServerService> logger) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 4096;
    private const int MaximumDataLineCharacters = 64 * 1024;
    private static readonly Encoding ProtocolEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private enum ListenerMode { Smtp, Submission, ImplicitTls }

    private sealed class SmtpSession
    {
        public required ListenerMode Mode { get; init; }
        public bool IsSecure { get; set; }
        public string? AuthenticatedUser { get; set; }
        public bool IsAuthenticated => AuthenticatedUser is not null;
        public string? Sender { get; set; }
        public List<string> Recipients { get; } = [];
        public StringBuilder DataBuilder { get; } = new();
        public int DataByteCount { get; set; }
        public bool MessageTooLarge { get; set; }
        public bool InDataMode { get; set; }

        public void Reset()
        {
            Sender = null;
            Recipients.Clear();
            DataBuilder.Clear();
            if (DataBuilder.Capacity > 4096)
                DataBuilder.Capacity = 4096;
            DataByteCount = 0;
            MessageTooLarge = false;
            InDataMode = false;
        }
    }

    private readonly ConcurrentDictionary<IPAddress, int> _connectionsPerIp = new();

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

        var currentCount = _connectionsPerIp.AddOrUpdate(remoteIp, 1, (_, c) => c + 1);
        if (currentCount > config.MaxConnectionsPerIp)
        {
            _connectionsPerIp.AddOrUpdate(remoteIp, 0, (_, c) => c - 1);
            logger.LogWarning("Rejected connection from {Endpoint}: too many connections", remoteLabel);
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
                var senderAuthorization = scope.ServiceProvider.GetRequiredService<ISenderAuthorizationService>();
                var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

                Stream stream = client.GetStream();

                if (mode == ListenerMode.ImplicitTls)
                {
                    if (config.TlsCertificatePath is null)
                        throw new InvalidOperationException("Implicit TLS requires a certificate.");

                    var cert = LoadCertificate(config);
                    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    await sslStream.AuthenticateAsServerAsync(cert);
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
                    senderAuthorization,
                    db,
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
        finally
        {
            _connectionsPerIp.AddOrUpdate(remoteIp, 0, (_, c) => Math.Max(0, c - 1));
        }
    }

    private async Task RunSmtpSessionAsync(
        BoundedLineReader reader, StreamWriter writer, SmtpSession session,
        IEmailService emailService, ISenderAuthorizationService senderAuthorization,
        EmailDbContext db, GlobalConfigDB config,
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
                    session.MessageTooLarge = true;
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
                    else
                    {
                        var raw = session.DataBuilder.ToString();

                        if (session.IsAuthenticated
                            && (!await senderAuthorization.CanSendAsAsync(
                                    session.AuthenticatedUser!, session.Sender!, timeout.Token)
                                || !senderAuthorization.HasMatchingFromAddress(raw, session.Sender!)))
                        {
                            await writer.WriteLineAsync("550 5.7.1 Sender identity is not authorized");
                            session.Reset();
                            continue;
                        }

                        // DKIM signing for authenticated outbound messages
                        var signedRaw = raw;
                        if (session.IsAuthenticated && config.EnableDkimSigning
                            && config.DkimPrivateKeyPath is not null && session.Sender is not null)
                        {
                            var senderDomain = session.Sender.Contains('@')
                                ? session.Sender[(session.Sender.IndexOf('@') + 1)..]
                                : config.SmtpHostname;
                            try
                            {
                                signedRaw = dkimSigningService.Sign(
                                    raw,
                                    senderDomain,
                                    config.DkimSelector,
                                    config.DkimPrivateKeyPath);
                            }
                            catch (DkimSigningException exception)
                            {
                                logger.LogError(exception, "DKIM signing failed for {Domain}", senderDomain);
                                await writer.WriteLineAsync("451 4.3.0 Message signing failed");
                                session.Reset();
                                continue;
                            }
                        }

                        // SPF/DKIM/DMARC verification for inbound messages
                        if (!session.IsAuthenticated && (config.EnableSpfCheck || config.EnableDmarcCheck))
                        {
                            var senderDomain = session.Sender?.Contains('@') == true
                                ? session.Sender[(session.Sender.IndexOf('@') + 1)..]
                                : string.Empty;

                            var (spfPass, dkimPass, dmarcPass) = await emailService.VerifyInboundAuthAsync(senderDomain, raw, clientIp);

                            // Prepend Authentication-Results header
                            var authResults = $"Authentication-Results: {config.SmtpHostname}; " +
                                              $"spf={(spfPass ? "pass" : "fail")}; " +
                                              $"dkim={(dkimPass ? "pass" : "none")}; " +
                                              $"dmarc={(dmarcPass ? "pass" : "fail")}";
                            signedRaw = authResults + "\r\n" + signedRaw;

                            if (config.EnableDmarcCheck && !dmarcPass)
                            {
                                await writer.WriteLineAsync("550 5.7.1 DMARC policy failure");
                                session.Reset();
                                continue;
                            }
                        }

                        var (delivered, relayed) = await DeliverToAllAsync(
                            emailService, session.Sender!, session.Recipients, signedRaw, config.AllowRelay && session.IsAuthenticated);

                        if (session.IsAuthenticated)
                            await emailService.SaveSentCopyAsync(session.Sender!, signedRaw);

                        await writer.WriteLineAsync(delivered + relayed > 0 ? "250 2.0.0 OK" : "550 5.1.1 Delivery failed");
                        session.Reset();
                    }
                }
                else
                {
                    var messageLine = line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line;
                    var lineByteCount = Encoding.UTF8.GetByteCount(messageLine) + 2;

                    if (!session.MessageTooLarge)
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
                    await WriteEhloAsync(writer, config, session.IsSecure);
                    break;

                case "HELO":
                    await writer.WriteLineAsync($"250 {config.SmtpHostname}");
                    break;

                case "AUTH":
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
                    await HandleAuthAsync(line, reader, writer, session, db, timeout.Token);
                    break;

                case "MAIL":
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
                    var sender = ExtractAddress(line);
                    if (session.IsAuthenticated
                        && !await senderAuthorization.CanSendAsAsync(
                            session.AuthenticatedUser!, sender, timeout.Token))
                    {
                        await writer.WriteLineAsync("553 5.7.1 Sender address is not authorized");
                        break;
                    }
                    session.Sender = sender;
                    await writer.WriteLineAsync("250 2.1.0 OK");
                    break;

                case "RCPT":
                    if (session.Sender is null)
                    {
                        await writer.WriteLineAsync("503 5.5.1 MAIL FROM required first");
                        break;
                    }
                    if (session.Recipients.Count >= config.MaxRecipientsPerMessage)
                    {
                        await writer.WriteLineAsync("452 4.5.3 Too many recipients");
                        break;
                    }
                    var rcpt = ExtractAddress(line);
                    var isLocal = await emailService.CanReceiveAsync(rcpt);
                    if (isLocal)
                    {
                        session.Recipients.Add(rcpt);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                    }
                    else if (session.IsAuthenticated && config.AllowRelay)
                    {
                        session.Recipients.Add(rcpt);
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

                        var cert = LoadCertificate(config);
                        var tlsStream = new SslStream(upgradableStream, leaveInnerStreamOpen: false);
                        await tlsStream.AuthenticateAsServerAsync(cert);

                        var tlsStreamReader = new StreamReader(tlsStream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                        var tlsReader = new BoundedLineReader(tlsStreamReader);
                        var tlsWriter = new StreamWriter(tlsStream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
                        {
                            AutoFlush = true,
                            NewLine = "\r\n"
                        };

                        session.Reset();
                        session.AuthenticatedUser = null;
                        session.IsSecure = true;

                        await RunSmtpSessionAsync(
                            tlsReader,
                            tlsWriter,
                            session,
                            emailService,
                            senderAuthorization,
                            db,
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

    private static async Task HandleAuthAsync(
        string line, BoundedLineReader reader, StreamWriter writer,
        SmtpSession session, EmailDbContext db, CancellationToken ct)
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
            await writer.WriteLineAsync("535 5.7.8 Authentication failed");
            return;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct);

        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            await writer.WriteLineAsync("535 5.7.8 Authentication failed");
            return;
        }

        session.AuthenticatedUser = user.Username;
        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
    }

    private static async Task<(int delivered, int relayed)> DeliverToAllAsync(
        IEmailService emailService, string sender, List<string> recipients, string rawMessage, bool allowRelay)
    {
        var delivered = 0;
        var relayed = 0;
        foreach (var recipient in recipients)
        {
            if (await emailService.CanReceiveAsync(recipient))
            {
                if (await emailService.DeliverAsync(sender, recipient, rawMessage))
                    delivered++;
            }
            else if (allowRelay)
            {
                if (await emailService.RelayAsync(sender, recipient, rawMessage))
                    relayed++;
            }
        }
        return (delivered, relayed);
    }

    private static string ExtractAddress(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        if (start >= 0 && end > start)
            return line[(start + 1)..end];

        var colonIdx = line.IndexOf(':');
        return colonIdx >= 0 ? line[(colonIdx + 1)..].Trim() : string.Empty;
    }
}
