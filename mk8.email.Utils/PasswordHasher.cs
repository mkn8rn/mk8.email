namespace mk8.email.Utils;

public static class PasswordHasher
{
    public const string Scheme = "BLF-CRYPT";
    public const string DovecotSchemePrefix = "{BLF-CRYPT}";
    private const int WorkFactor = 13;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return DovecotSchemePrefix + BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public static bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password)
            || string.IsNullOrEmpty(passwordHash)
            || !passwordHash.StartsWith(DovecotSchemePrefix, StringComparison.Ordinal))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash[DovecotSchemePrefix.Length..]);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
