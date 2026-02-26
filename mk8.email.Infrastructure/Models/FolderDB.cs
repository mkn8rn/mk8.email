using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("folders")]
public class FolderDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("uid_validity")]
    public int UidValidity { get; set; } = 1;

    [Column("next_uid")]
    public int NextUid { get; set; } = 1;

    [Column("highest_mod_seq")]
    public long HighestModSeq { get; set; }

    [MaxLength(64)]
    [Column("mailbox_id")]
    public string MailboxId { get; set; } = Guid.CreateVersion7().ToString("N");

    [Column("is_subscribed")]
    public bool IsSubscribed { get; set; } = true;

    [Column("inbox_id")]
    public Guid InboxId { get; set; }

    [ForeignKey(nameof(InboxId))]
    public InboxDB Inbox { get; set; } = null!;

    public ICollection<EmailDB> Emails { get; set; } = [];

    public ICollection<ExpungedUidDB> ExpungedUids { get; set; } = [];
}
