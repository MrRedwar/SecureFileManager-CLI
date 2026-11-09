using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Interfaces;

namespace SecureFileManager.Data.Identity;

public sealed class DatabaseConnectionCheck(IDbContextFactory<AppDbContext> factory) : IDatabaseConnectionCheck
{
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any();
    }
}
