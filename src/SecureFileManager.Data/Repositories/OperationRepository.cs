using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Data.Repositories;

public sealed class OperationRepository(IDbContextFactory<AppDbContext> factory) : RepositoryBase(factory), IOperationRepository
{
    public Task<RepositoryResult<Core.Journal.JournalPage>> QueryAsync(Guid userId, Core.Journal.JournalQuery query,
        CancellationToken ct = default) => ExecuteAsync<Core.Journal.JournalPage>(async db =>
    {
        if (userId == Guid.Empty || !query.IsValid)
            return RepositoryResult<Core.Journal.JournalPage>.Fail(RepositoryStatus.Validation);
        var rows = db.Operations.AsNoTracking().Where(x => x.UserId == userId);
        if (query.Type is { } type) rows = rows.Where(x => x.OperationType == type);
        if (query.Result is { } result) rows = rows.Where(x => x.Result == result);
        if (query.FromUtc is { } from) rows = rows.Where(x => x.OccurredAtUtc >= from);
        if (query.UntilUtc is { } until) rows = rows.Where(x => x.OccurredAtUtc < until);
        if (query.Search.Length != 0) rows = rows.Where(x => x.TargetPathSnapshot != null && x.TargetPathSnapshot.Contains(query.Search) ||
            x.ExternalPathSnapshot != null && x.ExternalPathSnapshot.Contains(query.Search));
        var ordered = query.Descending ? rows.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id) :
            rows.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id);
        var page = await ordered.Skip(query.Page * query.PageSize).Take(query.PageSize + 1).ToListAsync(ct);
        return RepositoryResult<Core.Journal.JournalPage>.Ok(new(page.Take(query.PageSize).ToArray(), page.Count > query.PageSize));
    }, ct);
    public Task<RepositoryResult<Operation>> AddAsync(Operation operation, CancellationToken cancellationToken = default) =>
        ExecuteAsync<Operation>(async db =>
        {
            if (operation.FileId is Guid fileId && !await db.Files.IgnoreQueryFilters().AnyAsync(
                x => x.Id == fileId && x.OwnerId == operation.UserId, cancellationToken))
                return RepositoryResult<Operation>.Fail(RepositoryStatus.Validation);
            db.Operations.Add(operation);
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<Operation>.Ok(operation);
        }, cancellationToken);

    public Task<RepositoryResult<IReadOnlyList<Operation>>> GetForUserAsync(Guid userId, int take = 100,
        CancellationToken cancellationToken = default) => ExecuteAsync<IReadOnlyList<Operation>>(async db =>
        {
            if (userId == Guid.Empty || take is < 1 or > 500)
                return RepositoryResult<IReadOnlyList<Operation>>.Fail(RepositoryStatus.Validation);
            return RepositoryResult<IReadOnlyList<Operation>>.Ok(await db.Operations.AsNoTracking()
                .Where(x => x.UserId == userId).OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
                .Take(take).ToListAsync(cancellationToken));
        }, cancellationToken);
}
