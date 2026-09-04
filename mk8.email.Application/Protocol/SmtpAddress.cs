namespace mk8.email.Application.Protocol;

internal static class SmtpAddress
{
    private const string AllowedAtomCharacters = "!#$%&'*+-/=?^_`{|}~";

    public static bool TryNormalize(string value, bool allowEmpty, out string address)
    {
        address = string.Empty;
        if (allowEmpty && value.Length == 0)
            return true;

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 254
            || !value.All(char.IsAscii)
            || value.ContainsAny(['\r', '\n', '<', '>', ' ', '\t']))
        {
            return false;
        }

        var separator = value.LastIndexOf('@');
        if (separator is <= 0 or > 64 || separator == value.Length - 1)
            return false;
        if (value.IndexOf('@') != separator)
            return false;

        var localPart = value[..separator];
        var domain = value[(separator + 1)..];
        if (localPart.StartsWith('.')
            || localPart.EndsWith('.')
            || localPart.Contains("..", StringComparison.Ordinal)
            || !localPart.All(IsLocalPartCharacter))
        {
            return false;
        }

        if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
            return false;

        address = $"{localPart.ToLowerInvariant()}@{domain.ToLowerInvariant()}";
        return true;
    }

    private static bool IsLocalPartCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value)
        || value == '.'
        || AllowedAtomCharacters.Contains(value, StringComparison.Ordinal);
}
