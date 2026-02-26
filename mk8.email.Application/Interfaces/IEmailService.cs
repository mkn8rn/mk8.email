namespace mk8.email.Application.Interfaces;

public interface IEmailService
{
    Task<bool> CanReceiveAsync(string recipient);
    Task<bool> DeliverAsync(string sender, string recipient, string rawMessage);
    Task SaveSentCopyAsync(string sender, string rawMessage);
    Task<bool> RelayAsync(string sender, string recipient, string rawMessage);
    string SignWithDkim(string rawMessage, string domain, string selector, string privateKeyPath);
    Task<(bool spfPass, bool dkimPass, bool dmarcPass)> VerifyInboundAuthAsync(string senderDomain, string rawMessage, string? clientIp);
}
