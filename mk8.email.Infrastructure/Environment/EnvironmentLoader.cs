using System.Reflection;
using System.Text.Json;

namespace mk8.email.Infrastructure.Environment;

public static class EnvironmentLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static EnvironmentConfig Load(bool isDevelopment = false)
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                       ?? AppContext.BaseDirectory;

        var envDir = Path.Combine(assemblyDir, "Environment");
        var fileName = isDevelopment ? ".dev.env" : ".env";
        var filePath = Path.Combine(envDir, fileName);

        if (!File.Exists(filePath))
        {
            var fallback = Path.Combine(envDir, ".env");
            if (!File.Exists(fallback))
                throw new FileNotFoundException(
                    $"Environment file not found. Searched: {filePath}, {fallback}");
            filePath = fallback;
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize environment file: {filePath}");
    }
}
