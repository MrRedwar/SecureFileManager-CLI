using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Data.Repositories;
namespace SecureFileManager.Data.Identity;

public sealed class IdentityStore(IDbContextFactory<AppDbContext> factory, Core.Journal.IAuditHealth? health = null) : IIdentityStore
{
    public async Task<AuthOutcome> ExecuteAsync(Func<IIdentityTransaction, Task<IdentityDecision>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var decision = await action(new IdentityTransaction(db, transaction));
                if (!decision.Commit) { await transaction.RollbackAsync(ct);
                    if (decision.Outcome.Status == AuthStatus.InfrastructureError) health?.Failed();
                    return decision.Outcome; }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                health?.Succeeded();
                return decision.Outcome;
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < 2) { ct.ThrowIfCancellationRequested(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            { return new(AuthStatus.Conflict, "Этот логин уже занят."); }
            catch (Exception ex) when (ex is SqlException or DbUpdateException or PersistenceValidationException ||
                ex is InvalidOperationException)
            { health?.Failed(); return AuthOutcome.Infrastructure(); }
        }
    }
    private static bool IsDeadlock(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is SqlException { Number: 1205 }) return true;
        return false;
    }

    private sealed class IdentityTransaction : IIdentityTransaction
    {
        private readonly AppDbContext _db;
        public IOperationRepository Audit { get; }
        public IdentityTransaction(AppDbContext db, IDbContextTransaction transaction)
        {
            _db = db;
            Audit = new OperationRepository(new JoinedFactory(db, transaction));
        }
        public Task<User?> FindByUsernameAsync(string username, CancellationToken ct) =>
            _db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK,HOLDLOCK) WHERE [Username] = {username}")
                .SingleOrDefaultAsync(ct);
        public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
            _db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
                .SingleOrDefaultAsync(ct);
        public async Task<IReadOnlyList<DateTime>> RecentCountedFailuresAsync(Guid id, DateTime since, int take, CancellationToken ct) =>
            await _db.Operations.Where(x => x.UserId == id && x.OperationType == OperationType.LOGIN_FAILED &&
                x.Details == "INVALID_PASSWORD_COUNTED" && x.OccurredAtUtc > since)
                .OrderByDescending(x => x.OccurredAtUtc).Take(Math.Clamp(take, 0, 100))
                .Select(x => x.OccurredAtUtc).ToListAsync(ct);
        public void Add(User user) => _db.Users.Add(user);
        public async Task FlushAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
    }
    private sealed class JoinedFactory(AppDbContext parent, IDbContextTransaction transaction) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(parent.Database.GetDbConnection()).Options);
            db.Database.UseTransaction(transaction.GetDbTransaction());
            return db;
        }
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(CreateDbContext()); }
    }
}
