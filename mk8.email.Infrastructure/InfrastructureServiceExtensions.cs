using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, EnvironmentConfig env)
    {
        services.AddSingleton(env);

        services.AddDbContext<EmailDbContext>(options =>
            options.UseNpgsql(env.BuildConnectionString()));

        return services;
    }
}
