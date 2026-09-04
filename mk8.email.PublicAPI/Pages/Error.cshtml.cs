using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace mk8.email.PublicAPI.Pages;

[AllowAnonymous]
public sealed class ErrorModel : PageModel;
