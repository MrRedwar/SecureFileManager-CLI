using System.Security.Cryptography;
using System.Text;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Services;

namespace SecureFileManager.Core.Files;

public enum ExternalChange { CreateFile, CreateDirectory, Copy, Move, Delete, Restore, CreateZip, ExtractZip }
public sealed record ExternalChangeRequest(ExternalChange Kind, string? Source, string Directory, string Name, string? ExpectedVersion = null);
public sealed record ExternalOperationEntry(string RelativePath, bool Directory, string Version, long Size, DateTime ModifiedUtc);
public sealed record ExternalOperationPlan(Guid Id, ExternalChange Kind, string? Source, string Target, string? Stage,
    IReadOnlyList<ExternalOperationEntry> Entries, Guid? TrashId = null);
public interface IExternalOperationWorkspace : IDisposable
{
    ExternalOperationPlan Prepare(ExternalChangeRequest request, CancellationToken ct);
    IReadOnlyList<ExternalOperationEntry> Publish(ExternalOperationPlan plan, CancellationToken ct);
    void Settle(ExternalOperationPlan plan, bool committed);
    IReadOnlyList<ExternalOperationPlan> Pending();
    ArchivePreview Preview(string path, CancellationToken ct) => throw new FileCommandException(FileStatus.Unsupported);
}
public interface IExternalOperationStorage { IExternalOperationWorkspace Open(); }
public interface IExternalFileOperationService
{
    Task<FileResult<bool>> ExecuteAsync(ExternalChangeRequest request, CancellationToken ct = default);
    Task<FileResult<bool>> RecoverAsync(CancellationToken ct = default);
    Task<FileResult<ArchivePreview>> PreviewAsync(string path, CancellationToken ct = default);
}
public static class ExternalOperationRules
{
    public const int Nodes = 1000;
    public const long FileBytes = 100L * 1024 * 1024;
    public const long TotalBytes = 1024L * 1024 * 1024;
    public static string Key(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    public static bool AuditCode(string? code) => code is "EXTERNAL_OP_CreateFile" or "EXTERNAL_OP_CreateDirectory" or
        "EXTERNAL_OP_Copy" or "EXTERNAL_OP_Move" or "EXTERNAL_OP_Delete" or "EXTERNAL_OP_Restore" or
        "EXTERNAL_OP_CreateZip" or "EXTERNAL_OP_ExtractZip" or "EXTERNAL_OP_PREVIEW" or "EXTERNAL_OP_SOURCE" or "EXTERNAL_OP_RECOVERED";
}
public sealed class ExternalFileOperationService(IUserSession session, IExternalDocumentStore store,
    IExternalOperationStorage storage, IAuditService audit, IClock clock) : IExternalFileOperationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed class Context(IExternalDocumentWork work, SessionIdentity actor, long revision)
    {
        public IExternalDocumentWork Work { get; } = work;
        public SessionIdentity Actor { get; } = actor;
        public long Revision { get; } = revision;
        public IExternalOperationWorkspace? Space { get; set; }
        public ExternalOperationPlan? Plan { get; set; }
    }
    private void Check(Context c)
    { if (session.Revision != c.Revision || session.Current?.Id != c.Actor.Id) throw new FileCommandException(FileStatus.Denied); }
    private Task Event(Context c, string path, Guid? document, string code, OperationType type, Guid correlation, CancellationToken ct)
    {
        Check(c);
        var item = SecurityAudit.Create(clock.UtcNow, c.Actor, type, OperationResult.Success, code);
        item.ExternalPathSnapshot = path; item.ExternalDocumentId = document; item.CorrelationId = correlation;
        return c.Work.AuditAsync(item, ct);
    }
    private async Task<FileResult<T>> Run<T>(Func<Context, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); } catch (OperationCanceledException) { return new(FileStatus.Cancelled); }
        var actor = session.Current; var revision = session.Revision; Context? c = null; var committed = false;
        try
        {
            if (actor is null) throw new FileCommandException(FileStatus.Denied);
            var value = await store.ExecuteAsync(actor.Id, async work =>
            {
                c = new(work, actor, revision); Check(c);
                c.Space = await Task.Run(storage.Open, ct);
                var result = await action(c, ct); Check(c); return result;
            }, ct);
            committed = true;
            if (c!.Plan is { } plan) await Task.Run(() => c.Space!.Settle(plan, true), CancellationToken.None);
            Check(c); return new(FileStatus.Success, value);
        }
        catch (Exception ex) when (ex is FileCommandException or FileStoreException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or OperationCanceledException or NotSupportedException or System.IO.InvalidDataException)
        {
            var status = ex switch
            {
                FileCommandException f => f.Status, FileStoreException => FileStatus.DatabaseUnavailable,
                System.IO.InvalidDataException => FileStatus.ArchiveInvalid,
                OperationCanceledException => FileStatus.Cancelled, UnauthorizedAccessException => FileStatus.Denied,
                _ => FileStatus.Unavailable
            };
            if (c?.Plan is { } plan)
                try
                {
                    var saved = committed || await store.CommittedAsync(plan.Id, plan.Target, CancellationToken.None);
                    await Task.Run(() => c.Space!.Settle(plan, saved), CancellationToken.None);
                    if (saved) status = FileStatus.Conflict;
                }
                catch { status = FileStatus.RecoveryRequired; }
            try
            {
                if (!await audit.RecordAsync(actor, status is FileStatus.Denied or FileStatus.Invalid ? OperationType.ACCESS_DENIED : OperationType.OPERATION_FAILED,
                    status is FileStatus.Denied or FileStatus.Invalid ? OperationResult.Denied : OperationResult.Failed,
                    "EXTERNAL_OPERATION_" + status, CancellationToken.None)) status = FileStatus.DatabaseUnavailable;
            }
            catch { status = FileStatus.DatabaseUnavailable; }
            return new(status);
        }
        finally { c?.Space?.Dispose(); _gate.Release(); }
    }
    public Task<FileResult<bool>> ExecuteAsync(ExternalChangeRequest request, CancellationToken ct = default) => Run(async (c, token) =>
    {
        if (!Enum.IsDefined(request.Kind)) throw new FileCommandException(FileStatus.Invalid);
        if ((await Task.Run(c.Space!.Pending, token)).Count != 0) throw new FileCommandException(FileStatus.RecoveryRequired);
        c.Plan = await Task.Run(() => c.Space!.Prepare(request, token), token);
        Check(c);
        var entries = await Task.Run(() => c.Space!.Publish(c.Plan, token), token);
        Check(c);
        var knownSource = c.Plan.Source is { } source && request.Kind is ExternalChange.Move or ExternalChange.Delete or ExternalChange.Restore
            ? await c.Work.FindTreeAsync(source, token) : [];
        foreach (var record in knownSource)
        {
            var relative = record.Path.Length == c.Plan.Source!.Length ? "" : record.Path[(c.Plan.Source.Length + 1)..];
            if (!entries.Any(x => !x.Directory && x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new FileCommandException(FileStatus.Conflict);
        }
        Guid? targetId = null;
        foreach (var entry in entries.Where(x => !x.Directory))
        {
            var target = entry.RelativePath == "" ? c.Plan.Target : c.Plan.Target + "\\" + entry.RelativePath;
            var key = ExternalOperationRules.Key(target);
            var record = knownSource.FirstOrDefault(x => x.Path.Equals(entry.RelativePath == "" ? c.Plan.Source : c.Plan.Source + "\\" + entry.RelativePath, StringComparison.OrdinalIgnoreCase));
            var existing = await c.Work.FindPathAsync(key, token);
            if (record is not null && existing is not null && record.Id != existing.Id) throw new FileCommandException(FileStatus.Conflict);
            record ??= existing ?? new ExternalDocument();
            record.Path = target; record.PathKey = key; record.SizeBytes = entry.Size; record.ModifiedAtUtc = entry.ModifiedUtc; record.Version = entry.Version;
            await c.Work.TrackAsync(record, token);
            if (entry.RelativePath == "") targetId = record.Id;
        }
        if (c.Plan.Source is { } origin)
            await Event(c, origin, request.Kind is ExternalChange.Move or ExternalChange.Delete or ExternalChange.Restore ? targetId : null, "EXTERNAL_OP_SOURCE", OperationType.FILE_READ, c.Plan.Id, token);
        await Event(c, c.Plan.Target, targetId, "EXTERNAL_OP_" + request.Kind,
            request.Kind switch { ExternalChange.CreateDirectory => OperationType.DIRECTORY_CREATED, ExternalChange.Delete => OperationType.FILE_DELETED,
                ExternalChange.Move or ExternalChange.Restore => OperationType.FILE_UPDATED, ExternalChange.CreateZip => OperationType.ZIP_CREATED,
                ExternalChange.ExtractZip => OperationType.ZIP_EXTRACTED, _ => OperationType.FILE_CREATED },
            c.Plan.Id, token);
        return true;
    }, ct);
    public Task<FileResult<bool>> RecoverAsync(CancellationToken ct = default) => Run(async (c, token) =>
    {
        foreach (var plan in await Task.Run(c.Space!.Pending, token))
        {
            Check(c);
            var committed = await store.CommittedAsync(plan.Id, plan.Target, token);
            await Task.Run(() => c.Space!.Settle(plan, committed), token);
            await Event(c, plan.Target, null, "EXTERNAL_OP_RECOVERED", OperationType.FILE_UPDATED, Guid.NewGuid(), token);
        }
        return true;
    }, ct);
    public Task<FileResult<ArchivePreview>> PreviewAsync(string path, CancellationToken ct = default) => Run(async (c, token) =>
    {
        if ((await Task.Run(c.Space!.Pending, token)).Count != 0) throw new FileCommandException(FileStatus.RecoveryRequired);
        var preview = await Task.Run(() => c.Space!.Preview(path, token), token);
        await Event(c, path, null, "EXTERNAL_OP_PREVIEW", OperationType.FILE_READ, Guid.NewGuid(), token);
        return preview;
    }, ct);
}
