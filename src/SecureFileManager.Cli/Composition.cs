using Microsoft.Extensions.DependencyInjection;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Journal;
using SecureFileManager.Core.Security;
using SecureFileManager.Core.Services;
using SecureFileManager.Data.DependencyInjection;
using SecureFileManager.Infrastructure.Windows.FileSystem;

namespace SecureFileManager.Cli;

public static class Composition
{
    public static IServiceCollection Create() => new ServiceCollection().AddSecureFileManagerData()
        .AddSingleton<IClock, SystemClock>()
        .AddSingleton<AuditHealth>().AddSingleton<IAuditHealth>(s => s.GetRequiredService<AuditHealth>())
        .AddSingleton<IJournalService, JournalService>()
        .AddSingleton<IPasswordSecurity>(_ => new PasswordSecurity())
        .AddSingleton(_ => new LoginProtectionPolicy())
        .AddSingleton<UserSession>().AddSingleton<IUserSession>(s => s.GetRequiredService<UserSession>())
        .AddSingleton(_ => PersonalStorage.FromConfiguration())
        .AddSingleton(_ => new ConfiguredStorageRoot(StorageConfiguration.LoadRoot()))
        .AddSingleton<IPersonalStorageProvisioner>(s => s.GetRequiredService<PersonalStorage>())
        .AddSingleton<IPathPolicy>(s => s.GetRequiredService<PersonalStorage>())
        .AddSingleton<IPhysicalFileStorage, WindowsFileStorage>()
        .AddSingleton<FileService>().AddSingleton<IFileService>(s => s.GetRequiredService<FileService>())
        .AddSingleton<IArchiveService>(s => s.GetRequiredService<FileService>())
        .AddSingleton<IStorageDiagnostics>(s => s.GetRequiredService<FileService>())
        .AddSingleton<IDriveService, WindowsDriveService>()
        .AddSingleton<IComputerDirectoryReader, WindowsComputerDirectoryReader>()
        .AddSingleton<IComputerBrowseService, ComputerBrowseService>()
        .AddSingleton<IExternalDocumentStorage, WindowsExternalDocumentStorage>()
        .AddSingleton<IExternalDocumentService, ExternalDocumentService>()
        .AddSingleton<IExternalOperationStorage, WindowsExternalOperationStorage>()
        .AddSingleton<IExternalFileOperationService, ExternalFileOperationService>()
        .AddSingleton<IFileAccessPolicy, PersonalFileAccessPolicy>()
        .AddSingleton<IAuditService, SecurityAudit>()
        .AddSingleton<IAuthenticationService, AuthenticationService>()
        .AddSingleton<IRegistrationService, RegistrationService>()
        .AddSingleton<IUserStorageService, UserStorageService>()
        .AddSingleton<StartupCheck>().AddSingleton<ConsoleApplication>();
}
