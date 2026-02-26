using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("global_config")]
public class GlobalConfigDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    // ?? General ??

    [Column("allow_registration")]
    public bool AllowRegistration { get; set; } = true;

    // ?? SMTP · Networking ??

    [Required]
    [MaxLength(255)]
    [Column("smtp_hostname")]
    public string SmtpHostname { get; set; } = "localhost";

    [Column("smtp_port")]
    public int SmtpPort { get; set; } = 25;

    [Column("smtp_submission_port")]
    public int SmtpSubmissionPort { get; set; } = 587;

    [Column("smtp_implicit_tls_port")]
    public int SmtpImplicitTlsPort { get; set; } = 465;

    // ?? SMTP · Protocol toggles ??

    [Column("enable_smtp")]
    public bool EnableSmtp { get; set; } = true;

    [Column("enable_submission")]
    public bool EnableSubmission { get; set; }

    [Column("enable_implicit_tls")]
    public bool EnableImplicitTls { get; set; }

    // ?? SMTP · TLS ??

    [Column("enable_starttls")]
    public bool EnableStartTls { get; set; }

    [Column("require_tls")]
    public bool RequireTls { get; set; }

    [MaxLength(1024)]
    [Column("tls_certificate_path")]
    public string? TlsCertificatePath { get; set; }

    [MaxLength(1024)]
    [Column("tls_certificate_key_path")]
    public string? TlsCertificateKeyPath { get; set; }

    // ?? Authentication ??

    [Required]
    [MaxLength(50)]
    [Column("password_hash_scheme")]
    public string PasswordHashScheme { get; set; } = "PBKDF2-SHA256";

    [Column("require_auth")]
    public bool RequireAuth { get; set; } = true;

    // ?? Message limits ??

    [Column("max_message_size_bytes")]
    public int MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024;

    [Column("max_recipients_per_message")]
    public int MaxRecipientsPerMessage { get; set; } = 100;

    [Column("connection_timeout_seconds")]
    public int ConnectionTimeoutSeconds { get; set; } = 300;

    [Column("max_connections_per_ip")]
    public int MaxConnectionsPerIp { get; set; } = 10;

    // ?? Relay ??

    [Column("allow_relay")]
    public bool AllowRelay { get; set; }

    // ?? DKIM / SPF / DMARC ??

    [MaxLength(1024)]
    [Column("dkim_private_key_path")]
    public string? DkimPrivateKeyPath { get; set; }

    [MaxLength(255)]
    [Column("dkim_selector")]
    public string DkimSelector { get; set; } = "default";

    [Column("enable_dkim_signing")]
    public bool EnableDkimSigning { get; set; }

    [Column("enable_spf_check")]
    public bool EnableSpfCheck { get; set; }

    [Column("enable_dmarc_check")]
    public bool EnableDmarcCheck { get; set; }

    // ?? IMAP ??

    [Column("enable_imap")]
    public bool EnableImap { get; set; } = true;

    [Column("imap_port")]
    public int ImapPort { get; set; } = 143;

    [Column("enable_imap_implicit_tls")]
    public bool EnableImapImplicitTls { get; set; }

    [Column("imap_implicit_tls_port")]
    public int ImapImplicitTlsPort { get; set; } = 993;

    // ?? Metadata ??

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

