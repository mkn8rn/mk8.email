using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("global_limits")]
public class GlobalLimitsDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("default_max_domains_per_company")]
    public int DefaultMaxDomainsPerCompany { get; set; }

    [Column("default_max_inboxes_per_company")]
    public int DefaultMaxInboxesPerCompany { get; set; }

    [Column("default_max_inboxes_per_domain")]
    public int DefaultMaxInboxesPerDomain { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
