namespace mk8.email.Infrastructure.Environment;

public static class DkimIdentityValidator
{
    public static bool IsValidDomain(string domain) =>
        Uri.CheckHostName(domain) == UriHostNameType.Dns && domain.Contains('.');

    public static bool IsValidSelector(string selector) =>
        selector.Length is >= 1 and <= 63
        && selector[0] != '-'
        && selector[^1] != '-'
        && selector.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
