using System.Text;

namespace mk8.email.Application.Protocol;

internal static class MailWireEncoding
{
    public static Encoding Instance { get; } = Encoding.Latin1;
}
