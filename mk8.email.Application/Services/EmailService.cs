using System.Net.Sockets;
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
            .AnyAsync(i => (i.Name == localPart || i.Name == "*")
                        && i.Address.Domain == domain
                        && i.Address.IsActive
                        && i.Address.Company.IsActive
                        && i.Owner.IsActive
                        && (i.Name != "*" || i.AliasForInboxId != null));
    }

    public async Task<bool> DeliverAsync(string sender, string recipient, string rawMessage)
    {
        var (localPart, domain) = ParseRecipient(recipient);
        if (localPart is null || domain is null)
            return false;

        var inbox = await db.Inboxes
            .AsNoTracking()
            .Where(i => (i.Name == localPart || i.Name == "*")
                     && i.Address.Domain == domain
                     && i.Address.IsActive
                     && i.Address.Company.IsActive
                     && i.Owner.IsActive
                     && (i.Name != "*" || i.AliasForInboxId != null))
            .OrderBy(i => i.Name == localPart ? 0 : 1)
            .FirstOrDefaultAsync();
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

    // ?? SPF / DKIM / DMARC verification (inbound) ??

    public Task<(bool spfPass, bool dkimPass, bool dmarcPass)> VerifyInboundAuthAsync(
        string senderDomain, string rawMessage, string? clientIp) =>
        Task.FromResult((false, false, false));

}
