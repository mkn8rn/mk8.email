using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Services;

public class SeederService(
    EmailDbContext db,
    EnvironmentConfig env,
    ILogger<SeederService> logger) : ISeederService
{
    public async Task SeedAsync()
    {
        await MigrateAsync();
        await SyncGlobalConfigAsync();
        await SeedSuperAdminAsync();
    }

    private async Task MigrateAsync()
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully.");
        }
    }

    private async Task SyncGlobalConfigAsync()
    {
        var config = await db.GlobalConfig.FirstOrDefaultAsync();
        if (config is null)
        {
            logger.LogWarning("GlobalConfig not found in database — skipping sync");
            return;
        }

        config.SmtpHostname = env.Smtp.Hostname;
        config.SmtpPort = env.Smtp.Port;
        config.SmtpSubmissionPort = env.Smtp.SubmissionPort;
        config.SmtpImplicitTlsPort = env.Smtp.ImplicitTlsPort;
        config.EnableSmtp = env.Smtp.EnableSmtp;
        config.EnableSubmission = env.Smtp.EnableSubmission;
        config.EnableImplicitTls = env.Smtp.EnableImplicitTls;
        config.EnableStartTls = env.Smtp.EnableStartTls;
        config.RequireTls = env.Smtp.RequireTls;
        config.RequireAuth = env.Smtp.RequireAuth;
        config.AllowRelay = env.Smtp.AllowRelay;

        config.EnableImap = env.Imap.EnableImap;
        config.ImapPort = env.Imap.Port;
        config.EnableImapImplicitTls = env.Imap.EnableImplicitTls;
        config.ImapImplicitTlsPort = env.Imap.ImplicitTlsPort;

        config.TlsCertificatePath = env.Tls.CertificatePath;
        config.TlsCertificateKeyPath = env.Tls.CertificateKeyPath;

        config.DkimPrivateKeyPath = env.Dkim.PrivateKeyPath;
        config.DkimSelector = env.Dkim.Selector;
        config.EnableDkimSigning = env.Dkim.EnableSigning;

        config.EnableSpfCheck = env.Security.EnableSpfCheck;
        config.EnableDmarcCheck = env.Security.EnableDmarcCheck;
        config.PasswordHashScheme = env.Security.PasswordHashScheme;

        config.MaxMessageSizeBytes = env.Limits.MaxMessageSizeBytes;
        config.MaxRecipientsPerMessage = env.Limits.MaxRecipientsPerMessage;
        config.ConnectionTimeoutSeconds = env.Limits.ConnectionTimeoutSeconds;
        config.MaxConnectionsPerIp = env.Limits.MaxConnectionsPerIp;

        config.AllowRegistration = env.General.AllowRegistration;

        config.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        logger.LogInformation("GlobalConfig synced from environment file");
    }

    private async Task SeedSuperAdminAsync()
    {
        if (await db.Users.AnyAsync(u => u.Role == nameof(UserRole.SuperAdmin)))
            return;

        var admin = new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = env.SuperAdmin.Username,
            PasswordHash = PasswordHasher.Hash(env.SuperAdmin.Password),
            Role = nameof(UserRole.SuperAdmin),
        };

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        logger.LogWarning(
            "Default SuperAdmin account created (username: {Username}). Change the password immediately.",
            env.SuperAdmin.Username);
    }
}
