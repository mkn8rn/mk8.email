using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.CLI;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Environment;

return await RunManagementCommandAsync(args);

static async Task<int> RunManagementCommandAsync(string[] arguments)
{
    if (!IsSupportedCommand(arguments))
    {
        WriteUsage();
        return 2;
    }

    if (arguments.Length == 4
        && arguments[0] == "--create-account"
        && !Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out _))
    {
        Console.Error.WriteLine("The account role is not valid.");
        return 2;
    }

    if (arguments.Length == 3
        && arguments[0] == "--set-domain-active"
        && !bool.TryParse(arguments[2], out _))
    {
        Console.Error.WriteLine("The domain state must be true or false.");
        return 2;
    }

    try
    {
        var isDevelopment = arguments.Contains("--dev", StringComparer.Ordinal)
            || arguments.Contains("--development", StringComparer.Ordinal)
            || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
        var environmentConfig = EnvironmentLoader.Load(isDevelopment);

        if (arguments.SequenceEqual(["--healthcheck"]))
        {
            using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await ServerHealthCheck.IsHealthyAsync(environmentConfig, healthTimeout.Token) ? 0 : 1;
        }

        if (arguments.SequenceEqual(["--initialize-empty-database"]))
        {
            using var host = BuildHost(arguments, environmentConfig, includeExperimentalServers: false);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IDatabaseInitializationService>()
                .InitializeEmptyDatabaseAsync();
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--ensure-domain")
        {
            using var host = BuildHost(arguments, environmentConfig, includeExperimentalServers: false);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .EnsureDomainAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 4 && arguments[0] == "--create-account")
        {
            var passwordPath = Path.GetFullPath(arguments[3]);
            if (!File.Exists(passwordPath))
            {
                Console.Error.WriteLine("The password file does not exist.");
                return 2;
            }

            var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
            _ = Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out var role);
            using var host = BuildHost(arguments, environmentConfig, includeExperimentalServers: false);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .CreateAccountAsync(arguments[1], password, role);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--set-catchall")
        {
            using var host = BuildHost(arguments, environmentConfig, includeExperimentalServers: false);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetCatchAllAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--set-domain-active")
        {
            _ = bool.TryParse(arguments[2], out var isActive);
            using var host = BuildHost(arguments, environmentConfig, includeExperimentalServers: false);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetDomainActiveAsync(arguments[1], isActive);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        using var protocolHost = BuildHost(arguments, environmentConfig, includeExperimentalServers: true);
        using (var scope = protocolHost.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISeederService>().SeedAsync();
        await protocolHost.RunAsync();
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"The management command failed: {exception.GetBaseException().Message}");
        return 1;
    }
}

static bool IsSupportedCommand(string[] arguments) =>
    arguments.SequenceEqual(["--healthcheck"])
    || arguments.SequenceEqual(["--initialize-empty-database"])
    || arguments.Length == 3 && arguments[0] == "--ensure-domain"
    || arguments.Length == 4 && arguments[0] == "--create-account"
    || arguments.Length == 3 && arguments[0] == "--set-catchall"
    || arguments.Length == 3 && arguments[0] == "--set-domain-active"
    || arguments.SequenceEqual(["--serve-experimental-protocols"]);

static void WriteUsage()
{
    Console.Error.WriteLine("Use one valid management command.");
    Console.Error.WriteLine("The experimental protocol servers require an explicit command.");
}

static IHost BuildHost(
    string[] arguments,
    EnvironmentConfig environmentConfig,
    bool includeExperimentalServers)
{
    var builder = Host.CreateApplicationBuilder(arguments);
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
    builder.Services.AddInfrastructure(environmentConfig);
    builder.Services.AddApplication();
    if (includeExperimentalServers)
        builder.Services.AddExperimentalMailProtocolServers();
    return builder.Build();
}
