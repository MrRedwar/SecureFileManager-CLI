using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Journal;

namespace SecureFileManager.Data.Files;

public sealed class ExternalDocumentStore(IDbContextFactory<AppDbContext> factory, IAuditHealth? health = null) : IExternalDocumentStore
{
    public async Task<T> ExecuteAsync<T>(Guid actor, Func<IExternalDocumentWork, Task<T>> action, CancellationToken ct)
    {
        var committing = false;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var user = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={actor}").SingleOrDefaultAsync(ct);
            if (user is not { IsActive: true }) throw new FileCommandException(FileStatus.Denied);
            var work = new Work(db);
            var result = await action(work);
            ct.ThrowIfCancellationRequested(); committing = true;
            await tx.CommitAsync(ct); if (work.Audited) health?.Succeeded(); return result;
        }
        catch (Exception ex) when (ex is SqlException or DbUpdateException or InvalidOperationException)
        { health?.Failed(); throw new FileStoreException(committing); }
        catch (OperationCanceledException) when (committing) { health?.Failed(); throw new FileStoreException(true); }
    }
    public async Task<bool> CommittedAsync(Guid correlation, string path, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Operations.AnyAsync(x => EF.Functions.Collate(x.ExternalPathSnapshot!, "Latin1_General_100_CI_AS") == path && x.CorrelationId == correlation && x.Result == OperationResult.Success &&
                (x.Details == "EXTERNAL_SAVED" || x.Details == "EXTERNAL_OP_CreateFile" || x.Details == "EXTERNAL_OP_CreateDirectory" || x.Details == "EXTERNAL_OP_Copy" || x.Details == "EXTERNAL_OP_Move" ||
                x.Details == "EXTERNAL_OP_Delete" || x.Details == "EXTERNAL_OP_Restore" || x.Details == "EXTERNAL_OP_CreateZip" || x.Details == "EXTERNAL_OP_ExtractZip"), ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException) { throw new FileStoreException(true); }
    }
    private sealed class Work(AppDbContext db) : IExternalDocumentWork
    {
        public bool Audited { get; private set; }
        public Task<ExternalDocument?> FindAsync(Guid id, CancellationToken ct) => db.ExternalDocuments.SingleOrDefaultAsync(x => x.Id == id, ct);
        public Task<ExternalDocument?> FindPathAsync(string key, CancellationToken ct) => db.ExternalDocuments.SingleOrDefaultAsync(x => x.PathKey == key, ct);
        public async Task<IReadOnlyList<ExternalDocument>> FindTreeAsync(string path, CancellationToken ct)
        {
            var result = await db.ExternalDocuments.Where(x => EF.Functions.Collate(x.Path, "Latin1_General_100_CI_AS") == path ||
                EF.Functions.Collate(x.Path, "Latin1_General_100_CI_AS").StartsWith(path + "\\")).Take(ExternalOperationRules.Nodes + 1).ToListAsync(ct);
            if (result.Count > ExternalOperationRules.Nodes) throw new FileCommandException(FileStatus.LimitExceeded);
            return result;
        }
        public async Task TrackAsync(ExternalDocument document, CancellationToken ct)
        {
            if (db.Entry(document).State == EntityState.Detached) db.ExternalDocuments.Add(document);
            await db.SaveChangesAsync(ct);
        }
        public async Task AuditAsync(Operation operation, CancellationToken ct) { db.Operations.Add(operation); await db.SaveChangesAsync(ct); Audited = true; }
    }
}
