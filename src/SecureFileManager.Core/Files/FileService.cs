using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Services;
namespace SecureFileManager.Core.Files;

public sealed partial class FileService(IUserSession session, IFileAccessPolicy access, IFileTransactionStore store,
    IPhysicalFileStorage physical, IAuditService audit, IClock clock, ZipLimits? zipLimits = null) : IFileService, IArchiveService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event EventHandler<FileChangeNotification>? Changed;
    public async Task WaitForIdleAsync() { await _gate.WaitAsync(); _gate.Release(); }
    private sealed class Context(IFileWork work, IFileWorkspace space, SessionIdentity actor, long revision)
    {
        public IFileWork Work { get; } = work;
        public IFileWorkspace Space { get; } = space;
        public SessionIdentity Actor { get; } = actor;
        public long Revision { get; } = revision;
        public FileJournal? Journal { get; set; }
        public bool MetadataChanged { get; set; }
    }
    private void Check(Context context)
    {
        if (session.Revision != context.Revision || !access.Allows(session.Current, context.Actor.Id))
            throw new FileCommandException(FileStatus.Denied);
    }
    private static T Require<T>(RepositoryResult<T> result)
    {
        if (result.IsSuccess) return result.Value!;
        throw new FileCommandException(result.Status switch
        {
            RepositoryStatus.NotFound => FileStatus.NotFound, RepositoryStatus.Conflict => FileStatus.Conflict,
            RepositoryStatus.Validation => FileStatus.Invalid, RepositoryStatus.Unauthorized => FileStatus.Denied,
            _ => FileStatus.DatabaseUnavailable
        });
    }
    private async Task<FileRecord> Record(Context c, Guid id, CancellationToken ct)
    {
        var result = await c.Work.Files.GetByIdAsync(c.Actor.Id, id, ct);
        if (result.Status == RepositoryStatus.NotFound) throw new FileCommandException(FileStatus.Denied);
        return Require(result);
    }
    private async Task Audit(Context c, OperationType type, string code, Guid? file, string? path, Guid correlation, CancellationToken ct,
        OperationResult result = OperationResult.Success)
    {
        Check(c);
        var item = SecurityAudit.Create(clock.UtcNow, c.Actor, type, result, code);
        item.FileId = file; item.TargetPathSnapshot = string.IsNullOrEmpty(path) ? null : path; item.CorrelationId = correlation;
        Require(await c.Work.Audit.AddAsync(item, ct));
    }
    private async Task<FileResult<T>> Run<T>(Func<Context, CancellationToken, Task<T>> action, bool recovery, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return new(FileStatus.Cancelled); }
        Context? context = null;
        var transactionCommitted = false;
        var actor = session.Current; var revision = session.Revision;
        try
        {
            if (actor is null) throw new FileCommandException(FileStatus.Denied);
            var value = await store.ExecuteAsync(actor.Id, async work =>
            {
                if (revision != session.Revision || !access.Allows(session.Current, actor.Id))
                    throw new FileCommandException(FileStatus.Denied);
                var space = await Task.Run(() => physical.Open(actor.Id), ct);
                context = new(work, space, actor, revision);
                if (!recovery && (await Task.Run(space.Pending, ct)).Count != 0)
                    throw new FileCommandException(FileStatus.RecoveryRequired);
                var result = await action(context, ct); Check(context); return result;
            }, ct);
            transactionCommitted = true;
            var changed = context is not null && (context.Journal is not null || context.MetadataChanged);
            if (context?.Journal is { } committed)
            {
                await Task.Run(() => context.Space.Settle(committed, true), CancellationToken.None);
                context.Journal = null;
            }
            if (context is not null) Check(context); // never return another session's read result after an awaited commit
            if (context is not null && changed)
            {
                // UI observers cannot invalidate an already committed file transaction.
                foreach (EventHandler<FileChangeNotification> observer in Changed?.GetInvocationList() ?? [])
                    try { observer(this, new(context.Actor.Id)); } catch { }
            }
            return new(FileStatus.Success, value);
        }
        catch (Exception error) when (error is FileCommandException or FileStoreException or System.IO.IOException or
            UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            var status = error switch
            {
                FileCommandException failure => failure.Status,
                FileStoreException => FileStatus.DatabaseUnavailable,
                System.Text.DecoderFallbackException or System.Text.EncoderFallbackException => FileStatus.Unsupported,
                ArgumentException => FileStatus.Invalid,
                OperationCanceledException => FileStatus.Cancelled,
                InvalidDataException => FileStatus.ArchiveInvalid,
                NotSupportedException => FileStatus.ArchiveUnsupported, _ => FileStatus.Unavailable
            };
            if (context?.Journal is { } journal)
            {
                try
                {
                    var committed = transactionCommitted || error is FileStoreException { CommitUncertain: true } &&
                        await store.HasSuccessAsync(actor!.Id, journal.Id, CancellationToken.None);
                    await Task.Run(() => context.Space.Settle(journal, committed), CancellationToken.None);
                    // An ambiguous response is never presented as a successful command; refresh after verification.
                    if (committed) status = FileStatus.Conflict;
                }
                catch { status = FileStatus.RecoveryRequired; }
            }
            try
            {
                if (error is FileCommandException { Validation: { } invalid, Format: { } format } && format != DocumentFormat.Txt)
                    if (!await audit.RecordAsync(actor, ValidationType(format), OperationResult.Failed, invalid.Code, CancellationToken.None))
                        status = FileStatus.DatabaseUnavailable;
                if (!await audit.RecordAsync(actor, status is FileStatus.Denied or FileStatus.Invalid or FileStatus.ArchiveUnsafe ?
                OperationType.ACCESS_DENIED : OperationType.OPERATION_FAILED,
                status is FileStatus.Denied or FileStatus.Invalid or FileStatus.ArchiveUnsafe ? OperationResult.Denied : OperationResult.Failed,
                "FILE_COMMAND_" + status, CancellationToken.None)) status = FileStatus.DatabaseUnavailable;
            }
            catch { status = FileStatus.DatabaseUnavailable; }
            return new(status, Validation: status == FileStatus.InvalidDocument ? (error as FileCommandException)?.Validation : null);
        }
        finally { context?.Space.Dispose(); _gate.Release(); }
    }
    private FileRecord NewRecord(Guid owner, PhysicalEntry item) => new()
    {
        OwnerId = owner, Name = item.Name, RelativePath = item.RelativePath, Kind = item.Kind,
        Extension = item.Kind == FileKind.File && item.Name.Contains('.') ? "." + item.Name.Split('.')[^1] : null,
        SizeBytes = item.SizeBytes, CreatedAtUtc = new[] { item.CreatedUtc, item.ModifiedUtc, clock.UtcNow }.Min(),
        ModifiedAtUtc = item.ModifiedUtc
    };
    private static void Match(FileRecord record, PhysicalEntry item)
    {
        if (record.Kind != item.Kind || record.SizeBytes != item.SizeBytes || record.ModifiedAtUtc != item.ModifiedUtc)
            throw new FileCommandException(FileStatus.Conflict);
    }
    private async Task DirectoryAllowed(Context c, string directory, CancellationToken ct)
    {
        FileRules.Path(directory, true);
        if (directory == "") return;
        var entry = Require(await c.Work.Files.GetByPathAsync(c.Actor.Id, directory, ct));
        if (entry.Kind != FileKind.Directory) throw new FileCommandException(FileStatus.Invalid);
    }
    private async Task TouchParents(Context c, string path, CancellationToken ct)
    {
        for (var parent = FileRules.Parent(path); parent != ""; parent = FileRules.Parent(parent))
        {
            var result = await c.Work.Files.GetByPathAsync(c.Actor.Id, parent, ct);
            var record = Require(result);
            var physical = await Task.Run(() => c.Space.Inspect(parent), ct);
            if (record.Kind != FileKind.Directory || physical.Kind != FileKind.Directory)
                throw new FileCommandException(FileStatus.Conflict);
            record.ModifiedAtUtc = physical.ModifiedUtc;
            Require(await c.Work.Files.UpdateAsync(record, ct));
        }
    }
    public Task<FileResult<IReadOnlyList<FileEntry>>> ListAsync(string directory, CancellationToken ct = default) =>
        Run<IReadOnlyList<FileEntry>>(async (c, token) =>
        {
            await DirectoryAllowed(c, directory, token);
            var records = Require(await c.Work.Files.ListChildrenAsync(c.Actor.Id, directory, token));
            if (records.Count > FileRules.ListLimit) throw new FileCommandException(FileStatus.LimitExceeded);
            IReadOnlyList<PhysicalEntry> disk;
            try { disk = await Task.Run(() => c.Space.List(directory), token); }
            catch (FileCommandException ex) when (ex.Status == FileStatus.NotFound && directory != "") { disk = []; }
            var byPath = records.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<FileEntry>();
            foreach (var item in disk)
            {
                byPath.TryGetValue(item.RelativePath, out var record);
                present.Add(item.RelativePath);
                var state = !item.IsSafe ? EntryState.Unsafe : record is null ? EntryState.Untracked :
                    record.Kind == item.Kind && record.SizeBytes == item.SizeBytes && record.ModifiedAtUtc == item.ModifiedUtc ?
                    EntryState.Managed : EntryState.Changed;
                rows.Add(new(record?.Id, item.RelativePath, item.Name, item.Kind, item.SizeBytes, item.ModifiedUtc, state));
            }
            rows.AddRange(records.Where(r => !present.Contains(r.RelativePath))
                .Select(r => new FileEntry(r.Id, r.RelativePath, r.Name, r.Kind, r.SizeBytes, r.ModifiedAtUtc, EntryState.Missing)));
            if (rows.Count > FileRules.ListLimit) throw new FileCommandException(FileStatus.LimitExceeded);
            await Audit(c, OperationType.FILE_READ, "DIRECTORY_LISTED", null, directory, Guid.NewGuid(), token);
            return rows;
        }, false, ct);
    public Task<FileResult<bool>> CreateAsync(string directory, string name, bool folder, CancellationToken ct = default) =>
        Mutate(directory, name, folder ? ChangeKind.Directory : ChangeKind.Create, null, null, ct);
    public Task<FileResult<bool>> ImportAsync(string directory, ImportSelection source, CancellationToken ct = default) =>
        Mutate(directory, source.Name, ChangeKind.Import, source, null, ct);
    private Task<FileResult<bool>> Mutate(string directory, string name, ChangeKind kind, ImportSelection? source, string? text, CancellationToken ct) =>
        Run(async (c, token) =>
        {
            await DirectoryAllowed(c, directory, token); var path = FileRules.Child(directory, name);
            if (kind == ChangeKind.Import && (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".website", StringComparison.OrdinalIgnoreCase)))
                throw new FileCommandException(FileStatus.Denied);
            if (kind == ChangeKind.Create)
            {
                FileRules.Text(name);
                text = DocumentValidator.Format(name) switch { DocumentFormat.Json => "{}", DocumentFormat.Xml => "<document />", _ => "" };
            }
            var existing = await c.Work.Files.GetByPathAsync(c.Actor.Id, path, token);
            if (existing.IsSuccess) throw new FileCommandException(FileStatus.Conflict);
            if (existing.Status != RepositoryStatus.NotFound) Require(existing);
            Check(c);
            c.Journal = await Task.Run(() => c.Space.Stage(Guid.NewGuid(), kind, path, text, source: source), token);
            Check(c); await Task.Run(() => c.Space.Publish(c.Journal), token);
            var entry = await Task.Run(() => c.Space.Inspect(path), token);
            var record = Require(await c.Work.Files.AddAsync(NewRecord(c.Actor.Id, entry), token));
            await Audit(c, kind == ChangeKind.Directory ? OperationType.DIRECTORY_CREATED : OperationType.FILE_CREATED,
                "FILE_TX_" + kind, record.Id, path, c.Journal.Id, token);
            await TouchParents(c, path, token); return true;
        }, false, ct);
    public Task<FileResult<TextDocument>> ReadAsync(Guid id, CancellationToken ct = default) => Read(id, false, ct);
    public Task<FileResult<TextDocument>> ReloadAsync(Guid id, CancellationToken ct = default) => Read(id, true, ct);
    private Task<FileResult<TextDocument>> Read(Guid id, bool reload, CancellationToken ct) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token); FileRules.Path(record.RelativePath);
        if (record.Kind != FileKind.File) throw new FileCommandException(FileStatus.Unsupported);
        FileRules.Text(record.Name);
        if (!reload) Match(record, await Task.Run(() => c.Space.Inspect(record.RelativePath), token));
        var text = await Task.Run(() => c.Space.Read(record.RelativePath), token);
        var validation = await Validate(c, record, text.Content, token);
        if (validation.Prohibited) throw new FileCommandException(FileStatus.InvalidDocument, validation, DocumentValidator.Format(record.Name));
        if (reload)
        {
            // Read and metadata refresh use the same workspace and SQL transaction.
            var item = await Task.Run(() => c.Space.Inspect(record.RelativePath), token);
            record.SizeBytes = item.SizeBytes; record.ModifiedAtUtc = item.ModifiedUtc;
            record = Require(await c.Work.Files.UpdateAsync(record, token));
            await Audit(c, OperationType.FILE_READ, "EDITOR_RELOADED", record.Id, record.RelativePath, Guid.NewGuid(), token);
            c.MetadataChanged = true;
        }
        await Audit(c, OperationType.FILE_READ, "TEXT_READ", record.Id, record.RelativePath, Guid.NewGuid(), token);
        return new TextDocument(record.Id, record.Name, text.Content, text.Version + "|" + Convert.ToBase64String(record.RowVersion)) { Validation = validation };
    }, false, ct);
    public Task<FileResult<bool>> SaveAsync(Guid id, string text, string version, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token); FileRules.Path(record.RelativePath); FileRules.Text(record.Name);
        if (record.Kind != FileKind.File || !version.EndsWith("|" + Convert.ToBase64String(record.RowVersion), StringComparison.Ordinal))
            throw new FileCommandException(FileStatus.Conflict);
        var validation = await Validate(c, record, text, token);
        if (!validation.IsValid) throw new FileCommandException(validation.Code == "SIZE" ? FileStatus.LimitExceeded : FileStatus.InvalidDocument,
            validation, DocumentValidator.Format(record.Name));
        Check(c);
        c.Journal = await Task.Run(() => c.Space.Stage(Guid.NewGuid(), ChangeKind.Save, record.RelativePath, text,
            version[..version.LastIndexOf('|')]), token);
        Check(c); await Task.Run(() => c.Space.Publish(c.Journal), token);
        var item = await Task.Run(() => c.Space.Inspect(record.RelativePath), token);
        record.SizeBytes = item.SizeBytes; record.ModifiedAtUtc = item.ModifiedUtc;
        Require(await c.Work.Files.UpdateAsync(record, token));
        await Audit(c, OperationType.FILE_UPDATED, "FILE_TX_SAVE", record.Id, record.RelativePath, c.Journal.Id, token);
        await TouchParents(c, record.RelativePath, token); return true;
    }, false, ct);
    private static OperationType ValidationType(DocumentFormat format) => format == DocumentFormat.Json ? OperationType.JSON_VALIDATED : OperationType.XML_VALIDATED;
    private async Task<DocumentValidation> Validate(Context c, FileRecord record, string text, CancellationToken ct)
    {
        var format = DocumentValidator.Format(record.Name);
        var result = await Task.Run(() => DocumentValidator.Validate(format, text), ct);
        if (format != DocumentFormat.Txt)
        {
            await Audit(c, ValidationType(format), result.Code, record.Id, record.RelativePath, Guid.NewGuid(), ct,
                result.IsValid ? OperationResult.Success : OperationResult.Failed);
            if (!result.IsValid)
                await Audit(c, OperationType.OPERATION_FAILED, "DOCUMENT_" + result.Code, record.Id, record.RelativePath, Guid.NewGuid(), ct, OperationResult.Failed);
        }
        return result;
    }
    public Task<FileResult<DocumentValidation>> ValidateAsync(Guid id, string text, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token); FileRules.Path(record.RelativePath); FileRules.Text(record.Name);
        if (record.Kind != FileKind.File) throw new FileCommandException(FileStatus.Unsupported);
        // Validation is authorized against the actual stored object, not a UI-supplied extension/path.
        await Task.Run(() => c.Space.Inspect(record.RelativePath), token);
        return await Validate(c, record, text, token);
    }, false, ct);
    public Task<FileResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token); FileRules.Path(record.RelativePath);
        Match(record, await Task.Run(() => c.Space.Inspect(record.RelativePath), token));
        if (record.Kind == FileKind.Directory && Require(await c.Work.Files.ListAsync(c.Actor.Id, token))
            .Any(x => x.RelativePath.StartsWith(record.RelativePath + "/", StringComparison.OrdinalIgnoreCase)))
            throw new FileCommandException(FileStatus.Conflict);
        Check(c); c.Journal = await Task.Run(() => c.Space.Stage(Guid.NewGuid(), ChangeKind.Delete, record.RelativePath), token);
        Check(c); await Task.Run(() => c.Space.Publish(c.Journal), token);
        Require(await c.Work.Files.SoftDeleteAsync(c.Actor.Id, record.Id, record.RowVersion, clock.UtcNow, token));
        await Audit(c, OperationType.FILE_DELETED, "FILE_TX_DELETE", record.Id, record.RelativePath, c.Journal.Id, token);
        await TouchParents(c, record.RelativePath, token); return true;
    }, false, ct);
    public Task<FileResult<bool>> SynchronizeAsync(string path, CancellationToken ct = default) => Run(async (c, token) =>
    {
        FileRules.Path(path); await DirectoryAllowed(c, FileRules.Parent(path), token);
        var current = await c.Work.Files.GetByPathAsync(c.Actor.Id, path, token);
        if (!current.IsSuccess && current.Status != RepositoryStatus.NotFound) Require(current);
        PhysicalEntry? item = null;
        try { item = await Task.Run(() => c.Space.Inspect(path), token); }
        catch (FileCommandException ex) when (ex.Status == FileStatus.NotFound) { }
        FileRecord record;
        if (item is null)
        {
            if (!current.IsSuccess) throw new FileCommandException(FileStatus.NotFound);
            record = Require(await c.Work.Files.SoftDeleteAsync(c.Actor.Id, current.Value!.Id, current.Value.RowVersion, clock.UtcNow, token));
        }
        else if (current.IsSuccess)
        {
            record = current.Value!;
            if (record.Kind != item.Kind) throw new FileCommandException(FileStatus.Conflict);
            record.SizeBytes = item.SizeBytes;
            // Repository keeps original CreatedAtUtc: reject timestamps older than creation instead of rewriting history.
            if (item.ModifiedUtc < current.Value!.CreatedAtUtc) throw new FileCommandException(FileStatus.Conflict);
            record.ModifiedAtUtc = item.ModifiedUtc;
            record = Require(await c.Work.Files.UpdateAsync(record, token));
        }
        else record = Require(await c.Work.Files.AddAsync(NewRecord(c.Actor.Id, item), token));
        await Audit(c, OperationType.FILE_UPDATED, "METADATA_SYNCHRONIZED", record.Id, path, Guid.NewGuid(), token);
        c.MetadataChanged = true;
        return true;
    }, false, ct);
    public Task<FileResult<bool>> RecoverAsync(CancellationToken ct = default) => Run(async (c, token) =>
    {
        foreach (var journal in await Task.Run(c.Space.Pending, token))
        {
            Check(c); var committed = await c.Work.HasSuccessAsync(journal.Id, token);
            await Task.Run(() => c.Space.Settle(journal, committed), token);
            await TouchParents(c, journal.RelativePath, token);
            await Audit(c, OperationType.FILE_UPDATED, committed ? "RECOVERY_FINALIZED" : "RECOVERY_ROLLED_BACK",
                null, journal.RelativePath, Guid.NewGuid(), token);
            c.MetadataChanged = true;
        }
        return true;
    }, true, ct);
}
