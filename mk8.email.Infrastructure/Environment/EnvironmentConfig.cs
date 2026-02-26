using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Environment;

public sealed class EnvironmentConfig
{
    public required DatabaseConfig Database { get; init; }
    public required SuperAdminConfig SuperAdmin { get; init; }
    public required SmtpConfig Smtp { get; init; }
    public required ImapConfig Imap { get; init; }
    public required TlsConfig Tls { get; init; }
    public required DkimConfig Dkim { get; init; }
    public required SecurityConfig Security { get; init; }
    public required LimitsConfig Limits { get; init; }
    public required GeneralConfig General { get; init; }

    public string BuildConnectionString() =>
        $"Host={Database.Host};Port={Database.Port};Database={Database.Name};Username={Database.Username};Password={Database.Password}";

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
    public string Password { get; init; } = "CHANGE_ME";
}

public sealed class SuperAdminConfig
{
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "CHANGE_ME";
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
    public string PasswordHashScheme { get; init; } = "PBKDF2-SHA256";
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
    public bool AllowRegistration { get; init; } = true;
}
