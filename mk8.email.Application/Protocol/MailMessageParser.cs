using System.Text;

namespace mk8.email.Application.Protocol;

internal readonly record struct ParsedMailMessage(
    string Subject,
    string Body,
    string Headers);

internal static class MailMessageParser
{
    public static ParsedMailMessage Parse(string rawMessage)
    {
        var separatorIndex = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separatorIndex < 0)
        {
            separatorIndex = rawMessage.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        var headers = separatorIndex >= 0
            ? rawMessage[..separatorIndex]
            : rawMessage;
        var body = separatorIndex >= 0
            ? rawMessage[(separatorIndex + separatorLength)..]
            : string.Empty;
        return new ParsedMailMessage(
            ExtractHeaderValue(headers, "Subject"),
            body,
            headers);
    }

    public static string ExtractHeaderValue(string headers, string fieldName)
    {
        var lines = headers.Split('\n');
        var value = new StringBuilder();
        var found = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            if (found && trimmed.Length > 0 && trimmed[0] is ' ' or '\t')
            {
                value.Append(' ').Append(trimmed.Trim());
                continue;
            }

            if (found)
                break;

            if (trimmed.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
            {
                value.Append(trimmed[(fieldName.Length + 1)..].Trim());
                found = true;
            }
        }

        return value.ToString();
    }
}
