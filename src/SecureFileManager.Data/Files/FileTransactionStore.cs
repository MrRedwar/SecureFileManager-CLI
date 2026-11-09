using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Data.Repositories;

namespace SecureFileManager.Data.Files;

public sealed class FileTransactionStore(IDbContextFactory<AppDbContext> factory, Core.Journal.IAuditHealth? health = null) : IFileTransactionStore
{
    public async Task<T> ExecuteAsync<T>(Guid owner, Func<IFileWork, Task<T>> work, CancellationToken ct)
    {
        var committing = false;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var active = await db.Users.FromSqlInterpolated(
                $"SELECT * FROM [Users] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {owner}").SingleOrDefaultAsync(ct);
            if (active is not { IsActive: true }) throw new FileCommandException(FileStatus.Denied);
            var context = new Work(db, transaction, owner);
            var result = await work(context);
            ct.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(ct);
            if (context.Audited) health?.Succeeded();
            return result;
        }
        catch (Exception ex) when (ex is SqlException or DbUpdateException or InvalidOperationException)
        { health?.Failed(); throw new FileStoreException(committing); }
        catch (FileCommandException ex) when (ex.Status == FileStatus.DatabaseUnavailable)
        { health?.Failed(); throw; }
        catch (OperationCanceledException) when (committing) { health?.Failed(); throw new FileStoreException(true); }
    }
    public async Task<bool> HasSuccessAsync(Guid owner, Guid correlationId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await Committed(db, owner, correlationId, ct);
        }
        catch (Exception ex) when (ex is SqlException or DbUpdateException or InvalidOperationException)
        { throw new FileStoreException(true); }
    }
    private static Task<bool> Committed(AppDbContext db, Guid owner, Guid id, CancellationToken ct) =>
        db.Operations.AnyAsync(x => x.UserId == owner && x.CorrelationId == id &&
            x.Result == OperationResult.Success && x.Details != null && x.Details.StartsWith("FILE_TX_"), ct);
    private sealed class Work : IFileWork
    {
        private readonly AppDbContext _db;
        private readonly Guid _owner;
        public IFileRepository Files { get; }
        public IOperationRepository Audit { get; }
        public bool Audited { get; private set; }
        public Work(AppDbContext db, IDbContextTransaction transaction, Guid owner)
        {
            _db = db; _owner = owner;
            var joined = new Joined(db, transaction);
            Files = new FileRepository(joined); Audit = new ObservedAudit(new OperationRepository(joined), () => Audited = true);
        }
        public Task<bool> HasSuccessAsync(Guid correlationId, CancellationToken ct) => Committed(_db, _owner, correlationId, ct);
    }
    private sealed class ObservedAudit(IOperationRepository inner, Action recorded) : IOperationRepository
    {
        public async Task<Core.Results.RepositoryResult<Operation>> AddAsync(Operation item, CancellationToken cancellationToken = default)
        {
            var result = await inner.AddAsync(item, cancellationToken);
            if (result.IsSuccess) recorded();
            return result;
        }
        public Task<Core.Results.RepositoryResult<IReadOnlyList<Operation>>> GetForUserAsync(Guid userId, int take = 100,
            CancellationToken cancellationToken = default) => inner.GetForUserAsync(userId, take, cancellationToken);
        public Task<Core.Results.RepositoryResult<Core.Journal.JournalPage>> QueryAsync(Guid userId, Core.Journal.JournalQuery query,
            CancellationToken ct = default) => inner.QueryAsync(userId, query, ct);
    }
    private sealed class Joined(AppDbContext parent, IDbContextTransaction transaction) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(parent.Database.GetDbConnection()).Options);
            db.Database.UseTransaction(transaction.GetDbTransaction()); return db;
        }
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
}
