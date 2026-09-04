using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Infrastructure.Environment;

public sealed class EnvironmentConfig
{
    public DatabaseConfig Database { get; init; } = new();
    public SmtpConfig Smtp { get; init; } = new();
    public ImapConfig Imap { get; init; } = new();
    public TlsConfig Tls { get; init; } = new();
    public DkimConfig Dkim { get; init; } = new();
    public SecurityConfig Security { get; init; } = new();
    public LimitsConfig Limits { get; init; } = new();
    public GeneralConfig General { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();

    public string BuildConnectionString()
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = Database.Host,
            Port = Database.Port,
            Database = Database.Name,
            Username = Database.Username,
            Password = Database.Password,
            ApplicationName = "mk8.email",
        }.ConnectionString;
    }

    public IReadOnlyList<string> Validate(bool isDevelopment = false)
    {
        var errors = new List<string>();

        RequireValue(errors, Database.Host, "Database.Host is required.");
        RequirePort(errors, Database.Port, "Database.Port");
        RequireValue(errors, Database.Name, "Database.Name is required.");
        RequireValue(errors, Database.Username, "Database.Username is required.");
        RequireSecret(errors, Database.Password, "Database.Password", isDevelopment);

        if (Uri.CheckHostName(Smtp.Hostname) != UriHostNameType.Dns)
            errors.Add("Smtp.Hostname must be a valid DNS name.");
        if (!isDevelopment && !Smtp.Hostname.Contains('.'))
            errors.Add("Smtp.Hostname must be a fully qualified DNS name in production.");

        var enabledPorts = new List<(string Name, int Port)>();
        AddEnabledPort(enabledPorts, Smtp.EnableSmtp, "Smtp.Port", Smtp.Port);
        AddEnabledPort(enabledPorts, Smtp.EnableSubmission, "Smtp.SubmissionPort", Smtp.SubmissionPort);
        AddEnabledPort(enabledPorts, Smtp.EnableImplicitTls, "Smtp.ImplicitTlsPort", Smtp.ImplicitTlsPort);
        AddEnabledPort(enabledPorts, Imap.EnableImap, "Imap.Port", Imap.Port);
        AddEnabledPort(enabledPorts, Imap.EnableImplicitTls, "Imap.ImplicitTlsPort", Imap.ImplicitTlsPort);

        if (enabledPorts.Count == 0)
            errors.Add("Enable at least one SMTP or IMAP listener.");

        foreach (var enabledPort in enabledPorts)
            RequirePort(errors, enabledPort.Port, enabledPort.Name);

        foreach (var duplicate in enabledPorts.GroupBy(item => item.Port).Where(group => group.Count() > 1))
            errors.Add($"Enabled listeners cannot share port {duplicate.Key}.");

        if (Smtp.EnableSubmission && !Smtp.EnableStartTls)
            errors.Add("SMTP submission requires STARTTLS.");
        if (Smtp.AllowRelay && !Smtp.RequireAuth)
            errors.Add("SMTP relay requires authentication.");
        if (Smtp.RequireTls && !Smtp.EnableStartTls && !Smtp.EnableImplicitTls)
            errors.Add("Smtp.RequireTls requires STARTTLS or implicit TLS.");
        if (!isDevelopment && Imap.EnableImap && !Smtp.EnableStartTls)
            errors.Add("The production IMAP listener requires STARTTLS.");

        var needsCertificate = Smtp.EnableStartTls || Smtp.EnableImplicitTls || Imap.EnableImplicitTls;
        if (needsCertificate)
            RequireFile(errors, Tls.CertificatePath, "Tls.CertificatePath");
        if (!string.IsNullOrWhiteSpace(Tls.CertificateKeyPath))
            RequireFile(errors, Tls.CertificateKeyPath, "Tls.CertificateKeyPath");
        if (Dkim.EnableSigning)
        {
            RequireFile(errors, Dkim.PrivateKeyPath, "Dkim.PrivateKeyPath");
            if (!DkimIdentityValidator.IsValidSelector(Dkim.Selector))
                errors.Add("Dkim.Selector must be a DNS label.");
        }

        if (!string.Equals(Security.PasswordHashScheme, "BLF-CRYPT", StringComparison.Ordinal))
            errors.Add("Security.PasswordHashScheme must be BLF-CRYPT.");
        if (!isDevelopment && (Security.EnableSpfCheck || Security.EnableDmarcCheck))
            errors.Add("The built-in SPF and DMARC checks are not approved for production.");

        if (Limits.MaxMessageSizeBytes < 64 * 1024)
            errors.Add("Limits.MaxMessageSizeBytes must be at least 65536.");
        if (Limits.MaxRecipientsPerMessage is < 1 or > 1000)
            errors.Add("Limits.MaxRecipientsPerMessage must be from 1 through 1000.");
        if (Limits.ConnectionTimeoutSeconds is < 10 or > 3600)
            errors.Add("Limits.ConnectionTimeoutSeconds must be from 10 through 3600.");
        if (Limits.MaxConnectionsPerIp is < 1 or > 10000)
            errors.Add("Limits.MaxConnectionsPerIp must be from 1 through 10000.");

        if (!isDevelopment && Admin.AllowedNetworks.Count == 0)
            errors.Add("Admin.AllowedNetworks must contain at least one network in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.DataProtectionKeyPath))
            errors.Add("Admin.DataProtectionKeyPath must be an absolute path in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.AuditLogPath))
            errors.Add("Admin.AuditLogPath must be an absolute path in production.");
        if (!isDevelopment && !Path.IsPathFullyQualified(Admin.HealthStatusPath))
            errors.Add("Admin.HealthStatusPath must be an absolute path in production.");
        if (Admin.SessionMinutes is < 5 or > 480)
            errors.Add("Admin.SessionMinutes must be from 5 through 480.");

        return errors;
    }

    private static void AddEnabledPort(List<(string Name, int Port)> ports, bool enabled, string name, int port)
    {
        if (enabled)
            ports.Add((name, port));
    }

    private static void RequireFile(List<string> errors, string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (!File.Exists(Path.GetFullPath(path)))
            errors.Add($"{name} does not exist.");
    }

    private static void RequirePort(List<string> errors, int port, string name)
    {
        if (port is < 1 or > 65535)
            errors.Add($"{name} must be from 1 through 65535.");
    }

    private static void RequireSecret(List<string> errors, string value, string name, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name} is required.");
            return;
        }

        if (!isDevelopment && value.Length < 16)
            errors.Add($"{name} must contain at least 16 characters in production.");
    }

    private static void RequireValue(List<string> errors, string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(message);
    }

    public GlobalConfigDB ToGlobalConfig() => new()
    {
        SmtpHostname = Smtp.Hostname,
        SmtpPort = Smtp.Port,
        SmtpSubmissionPort = Smtp.SubmissionPort,
        SmtpImplicitTlsPort = Smtp.ImplicitTlsPort,
        EnableSmtp = Smtp.EnableSmtp,
        EnableSubmission = Smtp.EnableSubmission,
        EnableImplicitTls = Smtp.EnableImplicitTls,
        EnableStartTls = Smtp.EnableStartTls,
        RequireTls = Smtp.RequireTls,
        RequireAuth = Smtp.RequireAuth,
        AllowRelay = Smtp.AllowRelay,

        EnableImap = Imap.EnableImap,
        ImapPort = Imap.Port,
        EnableImapImplicitTls = Imap.EnableImplicitTls,
        ImapImplicitTlsPort = Imap.ImplicitTlsPort,

        TlsCertificatePath = Tls.CertificatePath,
        TlsCertificateKeyPath = Tls.CertificateKeyPath,

        DkimPrivateKeyPath = Dkim.PrivateKeyPath,
        DkimSelector = Dkim.Selector,
        EnableDkimSigning = Dkim.EnableSigning,

        EnableSpfCheck = Security.EnableSpfCheck,
        EnableDmarcCheck = Security.EnableDmarcCheck,
        PasswordHashScheme = Security.PasswordHashScheme,

        MaxMessageSizeBytes = Limits.MaxMessageSizeBytes,
        MaxRecipientsPerMessage = Limits.MaxRecipientsPerMessage,
        ConnectionTimeoutSeconds = Limits.ConnectionTimeoutSeconds,
        MaxConnectionsPerIp = Limits.MaxConnectionsPerIp,

        AllowRegistration = General.AllowRegistration,
    };
}

public sealed class DatabaseConfig
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Name { get; init; } = "mk8email";
    public string Username { get; init; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public string? PasswordFile { get; init; }
}

public sealed class SmtpConfig
{
    public string Hostname { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public int SubmissionPort { get; init; } = 587;
    public int ImplicitTlsPort { get; init; } = 465;
    public bool EnableSmtp { get; init; } = true;
    public bool EnableSubmission { get; init; }
    public bool EnableImplicitTls { get; init; }
    public bool EnableStartTls { get; init; }
    public bool RequireTls { get; init; }
    public bool RequireAuth { get; init; } = true;
    public bool AllowRelay { get; init; }
}

public sealed class ImapConfig
{
    public int Port { get; init; } = 143;
    public int ImplicitTlsPort { get; init; } = 993;
    public bool EnableImap { get; init; } = true;
    public bool EnableImplicitTls { get; init; }
}

public sealed class TlsConfig
{
    public string? CertificatePath { get; init; }
    public string? CertificateKeyPath { get; init; }
}

public sealed class DkimConfig
{
    public string? PrivateKeyPath { get; init; }
    public string Selector { get; init; } = "default";
    public bool EnableSigning { get; init; }
}

public sealed class SecurityConfig
{
    public bool EnableSpfCheck { get; init; }
    public bool EnableDmarcCheck { get; init; }
    public string PasswordHashScheme { get; init; } = "BLF-CRYPT";
}

public sealed class LimitsConfig
{
    public int MaxMessageSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxRecipientsPerMessage { get; init; } = 100;
    public int ConnectionTimeoutSeconds { get; init; } = 300;
    public int MaxConnectionsPerIp { get; init; } = 10;
}

public sealed class GeneralConfig
{
    public bool AllowRegistration { get; init; }
}

public sealed class AdminConfig
{
    public IReadOnlyList<string> AllowedNetworks { get; init; } = [];
    public string DataProtectionKeyPath { get; init; } = "data-protection";
    public string AuditLogPath { get; init; } = "audit/admin.jsonl";
    public string HealthStatusPath { get; init; } = "health/status.json";
    public int SessionMinutes { get; init; } = 30;
}
