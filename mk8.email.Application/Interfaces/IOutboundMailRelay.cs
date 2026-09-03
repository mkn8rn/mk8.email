namespace mk8.email.Application.Interfaces;

public interface IOutboundMailRelay
{
    Task<bool> RelayAsync(string sender, string recipient, string rawMessage);
}
