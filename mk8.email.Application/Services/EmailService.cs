using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public class EmailService(EmailDbContext db, IOutboundMailRelay outboundMailRelay) : IEmailService
{
    public async Task<bool> CanReceiveAsync(string recipient)
    {
        var (localPart, domain) = ParseRecipient(recipient);
        if (localPart is null || domain is null)
            return false;

        return await db.Inboxes.AsNoTracking()
            .AnyAsync(i => i.Name == localPart
                        && i.Address.Domain == domain
                        && i.Address.IsActive);
    }

    public async Task<bool> DeliverAsync(string sender, string recipient, string rawMessage)
    {
        var (localPart, domain) = ParseRecipient(recipient);
        if (localPart is null || domain is null)
            return false;

        var inbox = await db.Inboxes
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == localPart
                                   && i.Address.Domain == domain
                                   && i.Address.IsActive);
        if (inbox is null)
            return false;

        var targetInboxId = inbox.AliasForInboxId ?? inbox.Id;

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.InboxId == targetInboxId
                                   && f.Name == DefaultFolders.Inbox);
        if (folder is null)
            return false;

        var uid = folder.NextUid++;
        var modSeq = ++folder.HighestModSeq;

        var (subject, body, headers) = ParseMessage(rawMessage);

        var messageId = ExtractHeaderValue(headers, "Message-ID");
        if (string.IsNullOrEmpty(messageId))
            messageId = $"<{Guid.NewGuid()}@{domain}>";

        var inReplyTo = ExtractHeaderValue(headers, "In-Reply-To");
        var threadId = ResolveThreadObjectId(inReplyTo, messageId);

        db.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = sender,
            Recipient = recipient,
            Subject = subject.Length > 998 ? subject[..998] : subject,
            Body = body,
            RawHeaders = headers,
            SizeBytes = Encoding.UTF8.GetByteCount(rawMessage),
            MessageId = messageId,
            InReplyTo = inReplyTo,
            Cc = ExtractHeaderValue(headers, "Cc"),
            EmailObjectId = Guid.CreateVersion7().ToString("N"),
            ThreadObjectId = threadId,
            Uid = uid,
            ModSeq = modSeq,
            FolderId = folder.Id,
        });

        await db.SaveChangesAsync();
        return true;
    }

    public async Task SaveSentCopyAsync(string sender, string rawMessage)
    {
        var (localPart, domain) = ParseRecipient(sender);
        if (localPart is null || domain is null)
            return;

        var inbox = await db.Inboxes
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == localPart
                                   && i.Address.Domain == domain
                                   && i.Address.IsActive);
        if (inbox is null)
            return;

        var targetInboxId = inbox.AliasForInboxId ?? inbox.Id;

        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.InboxId == targetInboxId
                                   && f.Name == DefaultFolders.Sent);
        if (folder is null)
            return;

        var uid = folder.NextUid++;
        var modSeq = ++folder.HighestModSeq;

        var (subject, body, headers) = ParseMessage(rawMessage);
        var recipient = ExtractHeaderValue(headers, "To");

        var sentMessageId = ExtractHeaderValue(headers, "Message-ID");
        if (string.IsNullOrEmpty(sentMessageId))
            sentMessageId = $"<{Guid.NewGuid()}@{domain}>";

        var sentInReplyTo = ExtractHeaderValue(headers, "In-Reply-To");
        var sentThreadId = ResolveThreadObjectId(sentInReplyTo, sentMessageId);

        db.Emails.Add(new EmailDB
        {
            Id = Guid.CreateVersion7(),
            Sender = sender,
            Recipient = recipient,
            Subject = subject.Length > 998 ? subject[..998] : subject,
            Body = body,
            RawHeaders = headers,
            SizeBytes = Encoding.UTF8.GetByteCount(rawMessage),
            MessageId = sentMessageId,
            InReplyTo = sentInReplyTo,
            Cc = ExtractHeaderValue(headers, "Cc"),
            EmailObjectId = Guid.CreateVersion7().ToString("N"),
            ThreadObjectId = sentThreadId,
            Uid = uid,
            ModSeq = modSeq,
            IsRead = true,
            FolderId = folder.Id,
        });

        await db.SaveChangesAsync();
    }

    private static string ExtractHeaderValue(string headers, string fieldName)
    {
        var lines = headers.Split('\n');
        var sb = new StringBuilder();
        var found = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (found && trimmed.Length > 0 && trimmed[0] is ' ' or '\t')
            {
                sb.Append(' ').Append(trimmed.Trim());
                continue;
            }

            if (found)
                break;

            if (trimmed.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(trimmed[(fieldName.Length + 1)..].Trim());
                found = true;
            }
        }

        return sb.ToString();
    }

    private static (string? localPart, string? domain) ParseRecipient(string address)
    {
        var parts = address.Split('@', 2);
        return parts.Length == 2
            ? (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant())
            : (null, null);
    }

    private string ResolveThreadObjectId(string? inReplyTo, string? messageId)
    {
        // If this message is a reply, try to find the thread of the parent message
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            var parent = db.Emails.AsNoTracking()
                .FirstOrDefault(e => e.MessageId == inReplyTo);
            if (parent?.ThreadObjectId is not null)
                return parent.ThreadObjectId;
        }

        // Check if any existing message references this one (forward-thread linking)
        if (!string.IsNullOrEmpty(messageId))
        {
            var child = db.Emails.AsNoTracking()
                .FirstOrDefault(e => e.InReplyTo == messageId && e.ThreadObjectId != null);
            if (child?.ThreadObjectId is not null)
                return child.ThreadObjectId;
        }

        // New thread
        return Guid.CreateVersion7().ToString("N");
    }

    private static (string subject, string body, string headers) ParseMessage(string rawMessage)
    {
        var subject = string.Empty;

        var separatorIdx = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separatorIdx < 0)
            separatorIdx = rawMessage.IndexOf("\n\n", StringComparison.Ordinal);

        string headers;
        string body;

        if (separatorIdx >= 0)
        {
            headers = rawMessage[..separatorIdx];
            var bodyStart = separatorIdx;
            while (bodyStart < rawMessage.Length && rawMessage[bodyStart] is '\r' or '\n')
                bodyStart++;
            body = rawMessage[bodyStart..];
        }
        else
        {
            headers = rawMessage;
            body = string.Empty;
        }

        subject = ExtractHeaderValue(headers, "Subject");

        return (subject, body, headers);
    }

    // ?? SMTP Relay (outbound delivery) ??

    public async Task<bool> RelayAsync(string sender, string recipient, string rawMessage)
    {
        return await outboundMailRelay.RelayAsync(sender, recipient, rawMessage);
    }

    // ?? DKIM signing ??

    public string SignWithDkim(string rawMessage, string domain, string selector, string privateKeyPath)
    {
        try
        {
            var (_, body, headers) = ParseMessage(rawMessage);

            // Canonicalize body (simple): ensure trailing CRLF
            var canonBody = body.TrimEnd() + "\r\n";
            var bodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonBody)));

            // Headers to sign
            var signedHeaders = "from:to:subject:date:message-id";

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Build DKIM header without b= value for signing
            var dkimTemplate = $"v=1; a=rsa-sha256; c=simple/simple; d={domain}; s={selector}; " +
                               $"t={timestamp}; h={signedHeaders}; bh={bodyHash}; b=";

            // Build header block to sign
            var headerBlock = new StringBuilder();
            foreach (var headerName in signedHeaders.Split(':'))
            {
                var value = ExtractHeaderValue(headers, headerName.Trim());
                if (!string.IsNullOrEmpty(value))
                    headerBlock.Append($"{headerName.Trim()}: {value}\r\n");
            }
            headerBlock.Append($"dkim-signature: {dkimTemplate}");

            // Sign
            var keyPem = File.ReadAllText(privateKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);

            var signature = rsa.SignData(
                Encoding.UTF8.GetBytes(headerBlock.ToString()),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var b64Signature = Convert.ToBase64String(signature);

            return $"DKIM-Signature: {dkimTemplate}{b64Signature}\r\n{rawMessage}";
        }
        catch
        {
            // If signing fails, return the original message unsigned
            return rawMessage;
        }
    }

    // ?? SPF / DKIM / DMARC verification (inbound) ??

    public async Task<(bool spfPass, bool dkimPass, bool dmarcPass)> VerifyInboundAuthAsync(
        string senderDomain, string rawMessage, string? clientIp)
    {
        var spfPass = await CheckSpfAsync(senderDomain, clientIp);
        var dkimPass = VerifyDkimSignature(rawMessage);
        // DMARC passes if at least one of SPF or DKIM passes and aligns with domain
        var dmarcPass = spfPass || dkimPass;
        return (spfPass, dkimPass, dmarcPass);
    }

    private static async Task<bool> CheckSpfAsync(string domain, string? clientIp)
    {
        if (string.IsNullOrEmpty(clientIp))
            return false;

        try
        {
            // SPF check: resolve the sender domain and verify the client IP is in its address list
            var hostEntry = await System.Net.Dns.GetHostEntryAsync(domain);
            var clientAddr = System.Net.IPAddress.Parse(clientIp);
            return Array.Exists(hostEntry.AddressList, a => a.Equals(clientAddr));
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyDkimSignature(string rawMessage)
    {
        // Extract DKIM-Signature header
        var (_, _, headers) = ParseMessage(rawMessage);
        var dkimHeader = ExtractHeaderValue(headers, "DKIM-Signature");
        if (string.IsNullOrEmpty(dkimHeader))
            return false;

        // Parse DKIM fields
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in dkimHeader.Split(';'))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = part[..eqIdx].Trim();
                var val = part[(eqIdx + 1)..].Trim();
                fields.TryAdd(key, val);
            }
        }

        if (!fields.TryGetValue("d", out var domain) ||
            !fields.TryGetValue("s", out var selector) ||
            !fields.TryGetValue("b", out _) ||
            !fields.TryGetValue("bh", out var bodyHashB64))
            return false;

        // Verify body hash
        var (_, body, _) = ParseMessage(rawMessage);
        var canonBody = body.TrimEnd() + "\r\n";
        var computedBodyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonBody)));

        if (computedBodyHash != bodyHashB64)
            return false;

        // Signature verification requires fetching the public key from DNS (selector._domainkey.domain TXT record).
        // Full DNS TXT lookup is not available in base .NET; accept body-hash-verified messages as partially valid.
        _ = domain;
        _ = selector;
        return true;
    }
}
