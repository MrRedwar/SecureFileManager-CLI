using SecureFileManager.Core.Entities;

namespace SecureFileManager.Core.Files;

public sealed record DiagnosticRecord(FileRecord File, bool HasAudit, bool LastDeleted);
public sealed record StorageIssue(string Code, string? Path);
public sealed record StorageDiagnosis(IReadOnlyList<StorageIssue> Issues, bool Complete, int CheckedObjects);
public interface IStorageDiagnostics
{
    Task<FileResult<StorageDiagnosis>> DiagnoseAsync(CancellationToken ct = default);
}

public sealed partial class FileService : IStorageDiagnostics
{
    public Task<FileResult<StorageDiagnosis>> DiagnoseAsync(CancellationToken ct = default) => Run(async (c, token) =>
    {
        const int limit = 10000;
        var issues = new List<StorageIssue>(); var complete = true; var checkedObjects = 0;
        try
        {
            foreach (var pending in await Task.Run(c.Space.Pending, token))
            {
                issues.Add(new("PENDING_OPERATION", pending.RelativePath));
                issues.Add(new(await c.Work.HasSuccessAsync(pending.Id, token) ? "PENDING_COMMITTED" : "PENDING_UNCOMMITTED", pending.RelativePath));
            }
        }
        catch (FileCommandException ex) when (ex.Status is FileStatus.RecoveryRequired or FileStatus.Invalid or FileStatus.Denied)
        { issues.Add(new("INCOMPLETE_STAGING", null)); }
        var records = Require(await c.Work.Files.DiagnosticAsync(c.Actor.Id, token));
        var metadataComplete = records.Count <= limit;
        if (records.Count > limit) { complete = false; records = records.Take(limit).ToArray(); }
        var active = records.Where(x => !x.File.IsDeleted).Select(x => x.File.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = records.GroupBy(x => x.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(r => r.File.IsDeleted).First(), StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Queue<string>(); directories.Enqueue("");
        while (directories.TryDequeue(out var directory))
        {
            Check(c); token.ThrowIfCancellationRequested();
            IReadOnlyList<PhysicalEntry> rows;
            try { rows = await Task.Run(() => c.Space.List(directory), token); }
            catch (FileCommandException ex) when (ex.Status is FileStatus.Unavailable or FileStatus.NotFound or FileStatus.Denied or FileStatus.LimitExceeded)
            { complete = false; issues.Add(new("DIRECTORY_UNAVAILABLE", directory)); continue; }
            foreach (var row in rows)
            {
                if (++checkedObjects > limit) { complete = false; break; }
                present.Add(row.RelativePath);
                if (!row.IsSafe) { issues.Add(new("UNSAFE_OBJECT", null)); continue; }
                if (!known.TryGetValue(row.RelativePath, out var record)) issues.Add(new(metadataComplete ? "UNTRACKED" : "METADATA_NOT_CHECKED", row.RelativePath));
                else if (record.File.IsDeleted && !active.Contains(row.RelativePath)) issues.Add(new("DELETED_PRESENT", row.RelativePath));
                else if (record.File.Kind != row.Kind || record.File.SizeBytes != row.SizeBytes || record.File.ModifiedAtUtc != row.ModifiedUtc)
                    issues.Add(new("METADATA_CHANGED", row.RelativePath));
                if (row.Kind == FileKind.Directory) directories.Enqueue(row.RelativePath);
            }
            if (checkedObjects > limit) break;
        }
        foreach (var record in records)
        {
            FileRules.Path(record.File.RelativePath);
            if (complete && !record.File.IsDeleted && !present.Contains(record.File.RelativePath))
                issues.Add(new("MISSING", record.File.RelativePath));
            if (!record.HasAudit || record.LastDeleted && !record.File.IsDeleted)
                issues.Add(new("AUDIT_METADATA_MISMATCH", record.File.RelativePath));
        }
        await Audit(c, OperationType.FILE_READ, complete ? "STORAGE_DIAGNOSED" : "STORAGE_DIAGNOSIS_PARTIAL",
            null, null, Guid.NewGuid(), token);
        return new StorageDiagnosis(issues, complete, Math.Min(checkedObjects, limit));
    }, true, ct);
}
