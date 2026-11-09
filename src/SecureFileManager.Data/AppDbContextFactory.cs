using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecureFileManager.Data.Configuration;

namespace SecureFileManager.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(DatabaseConfiguration.Load()).Options);
}
