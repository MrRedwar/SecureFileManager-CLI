using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Data;
using SecureFileManager.Data.Configuration;
using SecureFileManager.Data.DependencyInjection;

namespace SecureFileManager.Tests;

public sealed class DatabaseConfigurationTests
{
    [Theory]
    [InlineData(false, "Encrypt=True;TrustServerCertificate=True", false)]
    [InlineData(true, "Encrypt=True;TrustServerCertificate=True", true)]
    [InlineData(false, "Encrypt=True;TrustServerCertificate=False", true)]
    [InlineData(true, "Encrypt=False;TrustServerCertificate=True", false)]
    public void TlsAndEnvironmentPolicy(bool development, string options, bool allowed)
    {
        var text = @"Server=.\SQLEXPRESS;Database=SecureFileManagerDB;Integrated Security=True;" + options;
        if (allowed)
            Assert.NotEqual(SqlConnectionEncryptOption.Optional, new SqlConnectionStringBuilder(DatabaseConfiguration.Validate(text, development)).Encrypt);
        else
            Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.Validate(text, development));
    }

    [Fact]
    public void DevelopmentCannotTrustRemoteCertificateOrTargetSystemDatabase()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.Validate(
            "Server=remote;Database=SecureFileManagerDB;Encrypt=True;TrustServerCertificate=True", true));
        Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.Validate(
            @"Server=.\SQLEXPRESS;Database=master;Encrypt=True;TrustServerCertificate=False", false));
    }

    [Fact]
    public void CompositionRootResolvesFactoryAndAllRepositoriesWithoutOpeningSql()
    {
        using var services = new ServiceCollection().AddSecureFileManagerData()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(services.GetRequiredService<IUserRepository>());
        Assert.NotNull(services.GetRequiredService<IFileRepository>());
        Assert.NotNull(services.GetRequiredService<IOperationRepository>());
        using var db = services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
    }
}
