using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SecureFileManager.Cli;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--help"))
{
    Console.WriteLine("Secure File Manager CLI\n--check — только проверка SQL/схемы/StorageRoot, без записи.\nSFM_ENVIRONMENT, SFM_CONFIG_DIRECTORY, SFM_CONNECTION_STRING, SFM_STORAGE_ROOT — см. README.");
    return 0;
}
try
{
    using var services = Composition.Create().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    var check = await services.GetRequiredService<StartupCheck>().RunAsync();
    Console.WriteLine(check.Message);
    if (!check.Success) return 2;
    if (args.Contains("--check")) return 0;
    await services.GetRequiredService<ConsoleApplication>().RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Запуск не подтверждён. Проверьте SQL Server, схему, конфигурацию и доступ к StorageRoot. Подробности настройки — README.md. Автоматических миграций нет.");
    return 1;
}
