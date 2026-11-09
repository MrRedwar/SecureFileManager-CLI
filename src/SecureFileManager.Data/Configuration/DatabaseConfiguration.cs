using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SecureFileManager.Data.Configuration;

public static class DatabaseConfiguration
{
    public static string Load()
    {
        var environment = Environment.GetEnvironmentVariable("SFM_ENVIRONMENT") ?? "Production";
        if (environment is not ("Development" or "Production"))
            throw new InvalidOperationException("Неизвестное окружение приложения.");
        var development = environment == "Development";
        var directory = Environment.GetEnvironmentVariable("SFM_CONFIG_DIRECTORY") ?? AppContext.BaseDirectory;
        var file = Path.Combine(directory, development ? "appsettings.Development.json" : "appsettings.json");
        var connectionString = Environment.GetEnvironmentVariable("SFM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString) && File.Exists(file))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                connectionString = json.RootElement.GetProperty("ConnectionStrings").GetProperty("SecureFileManager").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Do not embed the document, provider exception, or connection string in diagnostics.
                throw new InvalidOperationException("Не удалось прочитать конфигурацию базы данных.");
            }
        }
        connectionString ??= "Server=.\\SQLEXPRESS;Database=SecureFileManagerDB;Integrated Security=True;Encrypt=True;TrustServerCertificate=False";
        return Validate(connectionString, development);
    }

    public static string Validate(string connectionString, bool development)
    {
        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Некорректная конфигурация базы данных."); }
        if (builder.Encrypt == SqlConnectionEncryptOption.Optional ||
            string.IsNullOrWhiteSpace(builder.InitialCatalog) ||
            new[] { "master", "model", "msdb", "tempdb" }.Contains(builder.InitialCatalog, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Требуются TLS и отдельная база приложения.");
        var localServers = new[] { @".\SQLEXPRESS", @"(local)\SQLEXPRESS", @"localhost\SQLEXPRESS", Environment.MachineName + @"\SQLEXPRESS" };
        if (builder.TrustServerCertificate && (!development || !localServers.Contains(builder.DataSource, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Доверие сертификату разрешено только для локального SQL Express в Development.");
        return builder.ConnectionString;
    }
}
