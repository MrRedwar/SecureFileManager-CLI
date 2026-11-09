using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Journal;

namespace SecureFileManager.Data.Identity;

public sealed class AuditProbe(IDbContextFactory<AppDbContext> factory) : IAuditProbe
{
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Exercise SELECT, INSERT, constraints and triggers. Never commit a synthetic audit event.
            _ = await db.Operations.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
            db.Operations.Add(new Operation { OperationType = OperationType.OPERATION_FAILED,
                Result = OperationResult.Failed, Details = "AUDIT_HEALTH_PROBE" });
            await db.SaveChangesAsync(ct);
            await transaction.RollbackAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
