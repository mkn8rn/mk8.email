using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.CLI;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Environment;

var isDev = args.Contains("--dev") || args.Contains("--development")
         || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";

var env = EnvironmentLoader.Load(isDev);

if (args.Contains("--healthcheck"))
{
    using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Environment.ExitCode = await ServerHealthCheck.IsHealthyAsync(env, healthTimeout.Token) ? 0 : 1;
    return;
}

Console.WriteLine($"mk8.email starting ({(isDev ? "Development" : "Production")})");
Console.WriteLine($"  Database : {env.Database.Host}:{env.Database.Port}/{env.Database.Name}");
Console.WriteLine($"  SMTP     : :{env.Smtp.Port} (enabled={env.Smtp.EnableSmtp})");
Console.WriteLine($"  IMAP     : :{env.Imap.Port} (enabled={env.Imap.EnableImap})");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddInfrastructure(env);
builder.Services.AddApplication();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<ISeederService>();
    await seeder.SeedAsync();
}

await host.RunAsync();
