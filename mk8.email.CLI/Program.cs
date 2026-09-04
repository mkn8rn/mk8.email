using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.CLI;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Environment;

var isDevelopment = args.Contains("--dev", StringComparer.Ordinal)
    || args.Contains("--development", StringComparer.Ordinal)
    || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
var environmentConfig = EnvironmentLoader.Load(isDevelopment);

if (args.SequenceEqual(["--healthcheck"]))
{
    using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Environment.ExitCode = await ServerHealthCheck.IsHealthyAsync(environmentConfig, healthTimeout.Token) ? 0 : 1;
    return;
}

if (args.SequenceEqual(["--initialize-empty-database"]))
{
    using var host = BuildHost(args, environmentConfig, includeExperimentalServers: false);
    using var scope = host.Services.CreateScope();
    var result = await scope.ServiceProvider
        .GetRequiredService<IDatabaseInitializationService>()
        .InitializeEmptyDatabaseAsync();
    Console.WriteLine(result.Message);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

if (args.Length == 3 && args[0] == "--ensure-domain")
{
    using var host = BuildHost(args, environmentConfig, includeExperimentalServers: false);
    using var scope = host.Services.CreateScope();
    var result = await scope.ServiceProvider
        .GetRequiredService<IMailAdministrationService>()
        .EnsureDomainAsync(args[1], args[2]);
    Console.WriteLine(result.Message);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

if (args.Length == 4 && args[0] == "--create-account")
{
    if (!Enum.TryParse<UserRole>(args[2], ignoreCase: true, out var role))
    {
        Console.Error.WriteLine("The account role is not valid.");
        Environment.ExitCode = 2;
        return;
    }

    var passwordPath = Path.GetFullPath(args[3]);
    if (!File.Exists(passwordPath))
    {
        Console.Error.WriteLine("The password file does not exist.");
        Environment.ExitCode = 2;
        return;
    }

    var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
    using var host = BuildHost(args, environmentConfig, includeExperimentalServers: false);
    using var scope = host.Services.CreateScope();
    var result = await scope.ServiceProvider
        .GetRequiredService<IMailAdministrationService>()
        .CreateAccountAsync(args[1], password, role);
    Console.WriteLine(result.Message);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

if (args.Length == 3 && args[0] == "--set-catchall")
{
    using var host = BuildHost(args, environmentConfig, includeExperimentalServers: false);
    using var scope = host.Services.CreateScope();
    var result = await scope.ServiceProvider
        .GetRequiredService<IMailAdministrationService>()
        .SetCatchAllAsync(args[1], args[2]);
    Console.WriteLine(result.Message);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

if (args.SequenceEqual(["--serve-experimental-protocols"]))
{
    using var host = BuildHost(args, environmentConfig, includeExperimentalServers: true);
    using (var scope = host.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<ISeederService>().SeedAsync();
    await host.RunAsync();
    return;
}

Console.Error.WriteLine(
    "Use one management command. The experimental protocol servers require an explicit command.");
Environment.ExitCode = 2;

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
