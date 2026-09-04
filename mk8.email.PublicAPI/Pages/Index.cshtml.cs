using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;

namespace mk8.email.PublicAPI.Pages;

[Authorize(Roles = nameof(UserRole.SuperAdmin))]
public sealed class IndexModel(IMailAdministrationService administration) : PageModel
{
    public int DomainCount { get; private set; }
    public int ActiveAccountCount { get; private set; }
    public int CatchAllCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var domains = await administration.GetDomainsAsync(cancellationToken);
        var accounts = await administration.GetAccountsAsync(cancellationToken);
        DomainCount = domains.Count(domain => domain.IsActive);
        ActiveAccountCount = accounts.Count(account => account.IsActive);
        CatchAllCount = domains.Count(domain => domain.CatchAllTarget is not null);
    }
}
