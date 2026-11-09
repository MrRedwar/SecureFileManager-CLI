using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Data.Repositories;

public sealed class FileRepository(IDbContextFactory<AppDbContext> factory) : RepositoryBase(factory), IFileRepository
{
    public Task<RepositoryResult<IReadOnlyList<Core.Files.DiagnosticRecord>>> DiagnosticAsync(Guid ownerId, CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<Core.Files.DiagnosticRecord>>(async db =>
        {
            if (ownerId == Guid.Empty) return RepositoryResult<IReadOnlyList<Core.Files.DiagnosticRecord>>.Fail(RepositoryStatus.Validation);
            var rows = await db.Files.IgnoreQueryFilters().AsNoTracking().Where(x => x.OwnerId == ownerId)
                .OrderBy(x => x.Id).Take(Core.Files.FileRules.ListLimit + 1)
                .Select(file => new Core.Files.DiagnosticRecord(file,
                    db.Operations.Any(o => o.UserId == ownerId && o.Result == OperationResult.Success &&
                        (o.FileId == file.Id && (o.OperationType == OperationType.FILE_CREATED || o.OperationType == OperationType.FILE_UPDATED ||
                            o.OperationType == OperationType.FILE_DELETED || o.OperationType == OperationType.DIRECTORY_CREATED ||
                            o.OperationType == OperationType.ZIP_CREATED) || o.OperationType == OperationType.ZIP_EXTRACTED && o.TargetPathSnapshot != null &&
                            (file.RelativePath == EF.Functions.Collate(o.TargetPathSnapshot, "Latin1_General_100_CI_AS") ||
                             file.RelativePath.StartsWith(EF.Functions.Collate(o.TargetPathSnapshot, "Latin1_General_100_CI_AS") + "/")))),
                    db.Operations.Where(o => o.UserId == ownerId && o.FileId == file.Id && o.Result == OperationResult.Success &&
                        (o.OperationType == OperationType.FILE_CREATED || o.OperationType == OperationType.FILE_UPDATED ||
                         o.OperationType == OperationType.FILE_DELETED || o.OperationType == OperationType.DIRECTORY_CREATED))
                        .OrderByDescending(o => o.OccurredAtUtc).ThenByDescending(o => o.Id)
                        .Select(o => o.OperationType == OperationType.FILE_DELETED).FirstOrDefault()))
                .ToListAsync(ct);
            return RepositoryResult<IReadOnlyList<Core.Files.DiagnosticRecord>>.Ok(rows);
        }, ct);
    public Task<RepositoryResult<FileRecord>> AddAsync(FileRecord file, CancellationToken cancellationToken = default) =>
        ExecuteAsync<FileRecord>(async db =>
        {
            if (file.IsDeleted) return RepositoryResult<FileRecord>.Fail(RepositoryStatus.Validation);
            db.Files.Add(file);
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<FileRecord>.Ok(file);
        }, cancellationToken);

    public Task<RepositoryResult<FileRecord>> GetByIdAsync(Guid ownerId, Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync<FileRecord>(async db => From(await db.Files.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == id && x.OwnerId == ownerId, cancellationToken)), cancellationToken);

    public Task<RepositoryResult<FileRecord>> GetByPathAsync(Guid ownerId, string relativePath, CancellationToken cancellationToken = default) =>
        ExecuteAsync<FileRecord>(async db => From(await db.Files.AsNoTracking().SingleOrDefaultAsync(
            x => x.OwnerId == ownerId && x.RelativePath == relativePath, cancellationToken)), cancellationToken);

    public Task<RepositoryResult<IReadOnlyList<FileRecord>>> ListAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<FileRecord>>(async db => RepositoryResult<IReadOnlyList<FileRecord>>.Ok(
            await db.Files.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.RelativePath).ToListAsync(cancellationToken)), cancellationToken);

    public Task<RepositoryResult<IReadOnlyList<FileRecord>>> ListChildrenAsync(Guid ownerId, string directory, CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<FileRecord>>(async db =>
        {
            var prefix = directory == "" ? "" : directory + "/";
            var query = db.Files.AsNoTracking().Where(x => x.OwnerId == ownerId);
            query = directory == "" ? query.Where(x => !x.RelativePath.Contains("/")) :
                query.Where(x => x.RelativePath.StartsWith(prefix) && !x.RelativePath.Substring(prefix.Length).Contains("/"));
            return RepositoryResult<IReadOnlyList<FileRecord>>.Ok(await query.OrderBy(x => x.RelativePath)
                .Take(SecureFileManager.Core.Files.FileRules.ListLimit + 1).ToListAsync(cancellationToken));
        }, cancellationToken);

    public Task<RepositoryResult<FileRecord>> UpdateAsync(FileRecord file, CancellationToken cancellationToken = default) =>
        ExecuteAsync<FileRecord>(async db =>
        {
            var stored = await db.Files.SingleOrDefaultAsync(x => x.Id == file.Id && x.OwnerId == file.OwnerId, cancellationToken);
            if (stored is null) return RepositoryResult<FileRecord>.Fail(RepositoryStatus.NotFound);
            if (file.RowVersion.Length != 8 || file.IsDeleted) return RepositoryResult<FileRecord>.Fail(RepositoryStatus.Validation);
            db.Entry(stored).Property(x => x.RowVersion).OriginalValue = file.RowVersion;
            stored.Name = file.Name;
            stored.RelativePath = file.RelativePath;
            stored.Kind = file.Kind;
            stored.Extension = file.Extension;
            stored.SizeBytes = file.SizeBytes;
            stored.ModifiedAtUtc = file.ModifiedAtUtc;
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<FileRecord>.Ok(stored);
        }, cancellationToken);

    public Task<RepositoryResult<FileRecord>> SoftDeleteAsync(Guid ownerId, Guid id, byte[] rowVersion,
        DateTime deletedAtUtc, CancellationToken cancellationToken = default) => ExecuteAsync<FileRecord>(async db =>
        {
            var file = await db.Files.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, cancellationToken);
            if (file is null) return RepositoryResult<FileRecord>.Fail(RepositoryStatus.NotFound);
            if (rowVersion.Length != 8) return RepositoryResult<FileRecord>.Fail(RepositoryStatus.Validation);
            db.Entry(file).Property(x => x.RowVersion).OriginalValue = rowVersion;
            file.IsDeleted = true;
            file.DeletedAtUtc = deletedAtUtc;
            file.ModifiedAtUtc = deletedAtUtc;
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<FileRecord>.Ok(file);
        }, cancellationToken);

    private static RepositoryResult<FileRecord> From(FileRecord? file) => file is null
        ? RepositoryResult<FileRecord>.Fail(RepositoryStatus.NotFound) : RepositoryResult<FileRecord>.Ok(file);
}
