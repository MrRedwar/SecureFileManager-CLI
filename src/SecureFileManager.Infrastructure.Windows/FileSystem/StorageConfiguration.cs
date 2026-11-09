using System.IO;
using System.Text.Json;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

// Uses the same environment, directory and JSON files as DatabaseConfiguration.
public static class StorageConfiguration
{
    public static string LoadRoot() => LoadRoot(
        Environment.GetEnvironmentVariable("SFM_ENVIRONMENT") ?? "Production",
        Environment.GetEnvironmentVariable("SFM_CONFIG_DIRECTORY") ?? AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("SFM_STORAGE_ROOT"));

    public static string LoadRoot(string environment, string directory, string? rootOverride = null)
    {
        if (environment is not ("Development" or "Production"))
            throw new InvalidOperationException("Неизвестное окружение приложения.");
        if (!string.IsNullOrWhiteSpace(rootOverride)) return rootOverride;

        var file = Path.Combine(directory, environment == "Development" ? "appsettings.Development.json" : "appsettings.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return RequireRoot(document.RootElement.GetProperty("StorageRoot").GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or
            IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Never include JSON contents, connection strings or provider exceptions.
            throw new InvalidOperationException("Не удалось прочитать StorageRoot. Укажите корень хранилища в конфигурации или SFM_STORAGE_ROOT. Автоматическая замена каталога запрещена.");
        }
    }

    private static string RequireRoot(string? root) => !string.IsNullOrWhiteSpace(root) ? root :
        throw new InvalidOperationException("StorageRoot должен быть задан явно.");
}
