using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("expunged_uids")]
public class ExpungedUidDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("uid")]
    public int Uid { get; set; }

    [Column("mod_seq")]
    public long ModSeq { get; set; }

    [Column("expunged_at")]
    public DateTime ExpungedAt { get; set; } = DateTime.UtcNow;

    [Column("folder_id")]
    public Guid FolderId { get; set; }

    [ForeignKey(nameof(FolderId))]
    public FolderDB Folder { get; set; } = null!;
}
