using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Files;

public sealed partial class FileService
{
    private readonly ZipLimits _zip = CheckedLimits(zipLimits);
    private static ZipLimits CheckedLimits(ZipLimits? value) { var limits = value ?? new(); limits.Check(); return limits; }
    private async Task Absent(Context c, string path, CancellationToken ct)
    {
        var result = await c.Work.Files.GetByPathAsync(c.Actor.Id, path, ct);
        if (result.IsSuccess) throw new FileCommandException(FileStatus.Conflict);
        if (result.Status != RepositoryStatus.NotFound) Require(result);
    }
    private static void ZipName(string name)
    { if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.ArchiveUnsupported); }
    private async Task<List<ArchiveSource>> Sources(Context c, IReadOnlyList<Guid> ids, string target, CancellationToken ct)
    {
        if (ids.Count == 0 || ids.Count > _zip.Entries) throw new FileCommandException(FileStatus.ArchiveLimit);
        var roots = new List<FileRecord>();
        foreach (var id in ids.Distinct()) roots.Add(await Record(c, id, ct));
        roots = roots.Where(r => !roots.Any(other => other.Id != r.Id && other.Kind == FileKind.Directory &&
            r.RelativePath.StartsWith(other.RelativePath + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        var result = new List<ArchiveSource>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        var queue = new Queue<(FileRecord Record, string Entry)>();
        foreach (var root in roots)
        {
            FileRules.Path(root.RelativePath);
            if (target.Equals(root.RelativePath, StringComparison.OrdinalIgnoreCase) || root.Kind == FileKind.Directory &&
                target.StartsWith(root.RelativePath + "/", StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.ArchiveUnsafe);
            queue.Enqueue((root, root.Name));
        }
        while (queue.TryDequeue(out var source))
        {
            ct.ThrowIfCancellationRequested(); Check(c); FileRules.Path(source.Record.RelativePath); ZipEngine.EntryPath(source.Entry);
            if (!names.Add(source.Entry)) throw new FileCommandException(FileStatus.ArchiveUnsafe);
            var item = await Task.Run(() => c.Space.Inspect(source.Record.RelativePath), ct); Match(source.Record, item);
            if (!item.IsSafe) throw new FileCommandException(FileStatus.Denied);
            if (result.Count >= _zip.Entries || item.SizeBytes > _zip.EntryBytes || (bytes += item.SizeBytes) > _zip.TotalBytes)
                throw new FileCommandException(FileStatus.ArchiveLimit);
            result.Add(new(item.RelativePath, source.Entry, item.Kind == FileKind.Directory));
            if (item.Kind != FileKind.Directory) continue;
            var disk = await Task.Run(() => c.Space.List(item.RelativePath), ct);
            var records = Require(await c.Work.Files.ListChildrenAsync(c.Actor.Id, item.RelativePath, ct));
            if (disk.Count != records.Count) throw new FileCommandException(FileStatus.Conflict);
            var byPath = records.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var child in disk)
            {
                if (!child.IsSafe) throw new FileCommandException(FileStatus.Denied);
                if (!byPath.TryGetValue(child.RelativePath, out var record)) throw new FileCommandException(FileStatus.Conflict);
                Match(record, child); queue.Enqueue((record, source.Entry + "/" + record.Name));
            }
        }
        return result;
    }
    public Task<FileResult<bool>> CreateArchiveAsync(IReadOnlyList<Guid> sources, string directory, string name,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => Run(async (c, token) =>
    {
        await DirectoryAllowed(c, directory, token); var path = FileRules.Child(directory, name); ZipName(name);
        await Absent(c, path, token);
        var items = await Sources(c, sources, path, token); Check(c);
        c.Journal = await Task.Run(() => c.Space.StageGenerated(Guid.NewGuid(), path, false, area =>
        {
            using var output = area.CreateFile("");
            ZipEngine.Create(output, items, source => c.Space.OpenBinary(source, _zip.EntryBytes), _zip, progress, token);
            if (output.Length > ZipLimits.ArchiveBytes) throw new FileCommandException(FileStatus.ArchiveLimit);
        }, token), token);
        Check(c); token.ThrowIfCancellationRequested(); await Task.Run(() => c.Space.Publish(c.Journal), token);
        var item = await Task.Run(() => c.Space.Inspect(path), token);
        var record = Require(await c.Work.Files.AddAsync(NewRecord(c.Actor.Id, item), token));
        await Audit(c, OperationType.ZIP_CREATED, "FILE_TX_ZIP_CREATED", record.Id, path, c.Journal.Id, token);
        await TouchParents(c, path, token); return true;
    }, false, ct);
    private async Task<FileRecord> ArchiveRecord(Context c, Guid id, CancellationToken ct)
    {
        var record = await Record(c, id, ct); FileRules.Path(record.RelativePath); ZipName(record.Name);
        if (record.Kind != FileKind.File) throw new FileCommandException(FileStatus.ArchiveUnsupported);
        Match(record, await Task.Run(() => c.Space.Inspect(record.RelativePath), ct)); return record;
    }
    public Task<FileResult<ArchivePreview>> PreviewArchiveAsync(Guid id, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await ArchiveRecord(c, id, token);
        using var input = await Task.Run(() => c.Space.OpenBinary(record.RelativePath, ZipLimits.ArchiveBytes), token);
        var preview = await Task.Run(() => ZipEngine.Inspect(input.Stream, _zip,
            input.Version + "|" + Convert.ToBase64String(record.RowVersion)), token);
        await Audit(c, preview.CanExtract ? OperationType.FILE_READ : OperationType.OPERATION_FAILED,
            preview.CanExtract ? "ZIP_PREVIEWED" : "ZIP_PREVIEW_REJECTED", record.Id, record.RelativePath, Guid.NewGuid(), token,
            preview.CanExtract ? OperationResult.Success : OperationResult.Denied);
        return preview;
    }, false, ct);
    public Task<FileResult<bool>> ExtractArchiveAsync(Guid id, string directory, string folderName, string expectedVersion,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) => Run(async (c, token) =>
    {
        await DirectoryAllowed(c, directory, token); var path = FileRules.Child(directory, folderName);
        await Absent(c, path, token); var record = await ArchiveRecord(c, id, token);
        using var input = await Task.Run(() => c.Space.OpenBinary(record.RelativePath, ZipLimits.ArchiveBytes), token);
        if (expectedVersion != input.Version + "|" + Convert.ToBase64String(record.RowVersion)) throw new FileCommandException(FileStatus.Conflict);
        Check(c);
        c.Journal = await Task.Run(() => c.Space.StageGenerated(Guid.NewGuid(), path, true,
            area => ZipEngine.Extract(input.Stream, area, _zip, progress, token), token), token);
        foreach (var child in c.Journal.Tree!) await Absent(c, path + "/" + child.Path, token);
        Check(c); token.ThrowIfCancellationRequested(); await Task.Run(() => c.Space.Publish(c.Journal), token);
        var paths = new[] { path }.Concat(c.Journal.Tree.Select(x => path + "/" + x.Path));
        foreach (var created in paths)
        {
            var item = await Task.Run(() => c.Space.Inspect(created), token);
            Require(await c.Work.Files.AddAsync(NewRecord(c.Actor.Id, item), token));
        }
        await Audit(c, OperationType.ZIP_EXTRACTED, "FILE_TX_ZIP_EXTRACTED", record.Id, path, c.Journal.Id, token);
        await TouchParents(c, path, token); return true;
    }, false, ct);
}
