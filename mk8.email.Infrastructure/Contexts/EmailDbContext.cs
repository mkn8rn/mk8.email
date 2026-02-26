using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Infrastructure.Data;

public class EmailDbContext(DbContextOptions<EmailDbContext> options) : DbContext(options)
{
    public DbSet<CompanyDB> Companies => Set<CompanyDB>();
    public DbSet<AddressDB> Addresses => Set<AddressDB>();
    public DbSet<UserDB> Users => Set<UserDB>();
    public DbSet<InboxDB> Inboxes => Set<InboxDB>();
    public DbSet<FolderDB> Folders => Set<FolderDB>();
    public DbSet<EmailDB> Emails => Set<EmailDB>();
    public DbSet<GlobalConfigDB> GlobalConfig => Set<GlobalConfigDB>();
    public DbSet<GlobalLimitsDB> GlobalLimits => Set<GlobalLimitsDB>();
    public DbSet<CompanyConfigDB> CompanyConfigs => Set<CompanyConfigDB>();
    public DbSet<CompanyLimitsDB> CompanyLimits => Set<CompanyLimitsDB>();
    public DbSet<ExpungedUidDB> ExpungedUids => Set<ExpungedUidDB>();

    private static readonly Guid GlobalConfigSeedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid GlobalLimitsSeedId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CompanyDB>(entity =>
        {
            entity.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<AddressDB>(entity =>
        {
            entity.HasIndex(a => a.Domain).IsUnique();

            entity.HasOne(a => a.Company)
                  .WithMany(c => c.Addresses)
                  .HasForeignKey(a => a.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserDB>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();

            entity.HasOne(u => u.Company)
                  .WithMany(c => c.Users)
                  .HasForeignKey(u => u.CompanyId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InboxDB>(entity =>
        {
            entity.HasIndex(i => new { i.AddressId, i.Name }).IsUnique();

            entity.HasOne(i => i.Address)
                  .WithMany(a => a.Inboxes)
                  .HasForeignKey(i => i.AddressId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.Owner)
                  .WithMany()
                  .HasForeignKey(i => i.OwnerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.AliasForInbox)
                  .WithMany()
                  .HasForeignKey(i => i.AliasForInboxId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FolderDB>(entity =>
        {
            entity.HasIndex(f => new { f.InboxId, f.Name }).IsUnique();

            entity.HasOne(f => f.Inbox)
                  .WithMany(i => i.Folders)
                  .HasForeignKey(f => f.InboxId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailDB>(entity =>
        {
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => new { e.FolderId, e.Uid }).IsUnique();
            entity.HasIndex(e => new { e.FolderId, e.ModSeq });
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.EmailObjectId);

            entity.HasOne(e => e.Folder)
                  .WithMany(f => f.Emails)
                  .HasForeignKey(e => e.FolderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExpungedUidDB>(entity =>
        {
            entity.HasIndex(eu => new { eu.FolderId, eu.Uid });
            entity.HasIndex(eu => new { eu.FolderId, eu.ModSeq });

            entity.HasOne(eu => eu.Folder)
                  .WithMany(f => f.ExpungedUids)
                  .HasForeignKey(eu => eu.FolderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyConfigDB>(entity =>
        {
            entity.HasIndex(cc => cc.CompanyId).IsUnique();

            entity.HasOne(cc => cc.Company)
                  .WithOne(c => c.Config)
                  .HasForeignKey<CompanyConfigDB>(cc => cc.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyLimitsDB>(entity =>
        {
            entity.HasIndex(cl => cl.CompanyId).IsUnique();

            entity.HasOne(cl => cl.Company)
                  .WithOne(c => c.Limits)
                  .HasForeignKey<CompanyLimitsDB>(cl => cl.CompanyId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalConfigDB>().HasData(new GlobalConfigDB
        {
            Id = GlobalConfigSeedId,
            AllowRegistration = true,
            SmtpHostname = "localhost",
            SmtpPort = 25,
            SmtpSubmissionPort = 587,
            SmtpImplicitTlsPort = 465,
            EnableSmtp = true,
            EnableSubmission = false,
            EnableImplicitTls = false,
            EnableStartTls = false,
            RequireTls = false,
            PasswordHashScheme = "PBKDF2-SHA256",
            RequireAuth = true,
            MaxMessageSizeBytes = 10 * 1024 * 1024,
            MaxRecipientsPerMessage = 100,
            ConnectionTimeoutSeconds = 300,
            MaxConnectionsPerIp = 10,
            AllowRelay = false,
            EnableImap = true,
            ImapPort = 143,
            EnableImapImplicitTls = false,
            ImapImplicitTlsPort = 993,
        });

        modelBuilder.Entity<GlobalLimitsDB>().HasData(new GlobalLimitsDB
        {
            Id = GlobalLimitsSeedId,
            DefaultMaxDomainsPerCompany = 0,
            DefaultMaxInboxesPerCompany = 0,
            DefaultMaxInboxesPerDomain = 0,
        });
    }
}
