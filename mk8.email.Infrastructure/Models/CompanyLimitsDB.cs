using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("company_limits")]
public class CompanyLimitsDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("company_id")]
    public Guid CompanyId { get; set; }

    [ForeignKey(nameof(CompanyId))]
    public CompanyDB Company { get; set; } = null!;

    [Column("max_domains")]
    public int? MaxDomains { get; set; }

    [Column("max_inboxes")]
    public int? MaxInboxes { get; set; }

    [Column("max_inboxes_per_domain")]
    public int? MaxInboxesPerDomain { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
