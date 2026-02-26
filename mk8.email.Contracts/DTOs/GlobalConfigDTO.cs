namespace mk8.email.Contracts.DTOs;

public record GlobalConfigDTO(
    Guid Id,

    // General
    bool AllowRegistration,

    // SMTP · Networking
    string SmtpHostname,
    int SmtpPort,
    int SmtpSubmissionPort,
    int SmtpImplicitTlsPort,

    // SMTP · Protocol toggles
    bool EnableSmtp,
    bool EnableSubmission,
    bool EnableImplicitTls,

    // SMTP · TLS
    bool EnableStartTls,
    bool RequireTls,
    string? TlsCertificatePath,
    string? TlsCertificateKeyPath,

    // Authentication
    string PasswordHashScheme,
    bool RequireAuth,

    // Message limits
    int MaxMessageSizeBytes,
    int MaxRecipientsPerMessage,
    int ConnectionTimeoutSeconds,
    int MaxConnectionsPerIp,

    // Relay
    bool AllowRelay,

    // IMAP
    bool EnableImap,
    int ImapPort,
    bool EnableImapImplicitTls,
    int ImapImplicitTlsPort);

