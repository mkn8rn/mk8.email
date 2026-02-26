using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("company_config")]
public class CompanyConfigDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("company_id")]
    public Guid CompanyId { get; set; }

    [ForeignKey(nameof(CompanyId))]
    public CompanyDB Company { get; set; } = null!;

    [Column("allow_user_registration")]
    public bool AllowUserRegistration { get; set; } = true;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
