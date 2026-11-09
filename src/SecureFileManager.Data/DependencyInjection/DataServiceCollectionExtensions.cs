using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Data.Configuration;
using SecureFileManager.Data.Repositories;

namespace SecureFileManager.Data.DependencyInjection;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddSecureFileManagerData(this IServiceCollection services)
    {
        var connectionString = DatabaseConfiguration.Load();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlServer(connectionString));
        services.AddTransient<IUserRepository, UserRepository>();
        services.AddTransient<IFileRepository, FileRepository>();
        services.AddTransient<IOperationRepository, OperationRepository>();
        services.AddSingleton<Core.Journal.IAuditProbe, Identity.AuditProbe>();
        services.AddSingleton<IIdentityStore, SecureFileManager.Data.Identity.IdentityStore>();
        services.AddSingleton<SecureFileManager.Core.Files.IFileTransactionStore, SecureFileManager.Data.Files.FileTransactionStore>();
        services.AddSingleton<Core.Files.IExternalDocumentStore, Files.ExternalDocumentStore>();
        services.AddSingleton<IDatabaseConnectionCheck, SecureFileManager.Data.Identity.DatabaseConnectionCheck>();
        return services;
    }
}
