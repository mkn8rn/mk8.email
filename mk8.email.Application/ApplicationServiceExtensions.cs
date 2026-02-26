using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;

namespace mk8.email.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IInboxService, InboxService>();
        services.AddScoped<IAddressService, AddressService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<ISeederService, SeederService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddHostedService<SmtpServerService>();
        services.AddHostedService<ImapServerService>();

        return services;
    }
}
