using System.Security.Cryptography;
using System.Text;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Services;

namespace SecureFileManager.Core.Files;

public sealed class ExternalDocumentService(IUserSession session, IExternalDocumentStore store,
    IExternalDocumentStorage storage, IAuditService audit, IClock clock) : IExternalDocumentService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed class Context(IExternalDocumentWork work, SessionIdentity actor, long revision)
    {
        public IExternalDocumentWork Work { get; } = work;
        public SessionIdentity Actor { get; } = actor;
        public long Revision { get; } = revision;
        public IExternalDocumentFile? File { get; set; }
    }
    private void Check(Context c)
    { if (session.Revision != c.Revision || session.Current?.Id != c.Actor.Id) throw new FileCommandException(FileStatus.Denied); }
    private async Task Event(Context c, ExternalDocument d, OperationType type, string code, OperationResult result, Guid correlation, CancellationToken ct)
    {
        Check(c);
        var operation = SecurityAudit.Create(clock.UtcNow, c.Actor, type, result, code);
        operation.ExternalDocumentId = d.Id; operation.ExternalPathSnapshot = d.Path; operation.CorrelationId = correlation;
        await c.Work.AuditAsync(operation, ct);
    }
    private async Task<FileResult<T>> Run<T>(Func<Context, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); } catch (OperationCanceledException) { return new(FileStatus.Cancelled); }
        Context? c = null; var committed = false; var actor = session.Current; var revision = session.Revision;
        try
        {
            if (actor is null) throw new FileCommandException(FileStatus.Denied);
            var value = await store.ExecuteAsync(actor.Id, async work =>
            {
                c = new(work, actor, revision); Check(c);
                var result = await action(c, ct); Check(c); return result;
            }, ct);
            committed = true;
            if (c!.File?.Pending is not null) await Task.Run(() => c.File.Settle(true), CancellationToken.None);
            Check(c); return new(FileStatus.Success, value);
        }
        catch (Exception ex) when (ex is FileCommandException or FileStoreException or IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException or OperationCanceledException)
        {
            var status = ex switch
            {
                FileCommandException f => f.Status, FileStoreException => FileStatus.DatabaseUnavailable,
                DecoderFallbackException or EncoderFallbackException => FileStatus.Unsupported,
                OperationCanceledException => FileStatus.Cancelled, UnauthorizedAccessException => FileStatus.Denied,
                _ => FileStatus.Unavailable
            };
            if (c?.File?.Pending is { } correlation)
            {
                try
                {
                    var saved = committed || await store.CommittedAsync(correlation, c.File.Path, CancellationToken.None);
                    await Task.Run(() => c.File.Settle(saved), CancellationToken.None);
                    if (saved) status = FileStatus.Conflict;
                }
                catch { status = FileStatus.RecoveryRequired; }
            }
            try
            {
                if (ex is FileCommandException { Validation: { }, Format: { } format })
                    if (!await audit.RecordAsync(actor, format == DocumentFormat.Json ? OperationType.JSON_VALIDATED : OperationType.XML_VALIDATED,
                        OperationResult.Failed, "EXTERNAL_REJECTED", CancellationToken.None)) status = FileStatus.DatabaseUnavailable;
                if (!await audit.RecordAsync(actor, status is FileStatus.Denied or FileStatus.Invalid ? OperationType.ACCESS_DENIED : OperationType.OPERATION_FAILED,
                    status is FileStatus.Denied or FileStatus.Invalid ? OperationResult.Denied : OperationResult.Failed,
                    "EXTERNAL_" + status, CancellationToken.None)) status = FileStatus.DatabaseUnavailable;
            }
            catch { status = FileStatus.DatabaseUnavailable; }
            return new(status, Validation: status == FileStatus.InvalidDocument ? (ex as FileCommandException)?.Validation : null);
        }
        finally { c?.File?.Dispose(); _gate.Release(); }
    }
    private static void Update(ExternalDocument record, ExternalText text)
    { record.Version = text.Version; record.SizeBytes = text.SizeBytes; record.ModifiedAtUtc = text.ModifiedUtc; }
    private async Task<ExternalDocument> Record(Context c, Guid id, CancellationToken ct) =>
        await c.Work.FindAsync(id, ct) ?? throw new FileCommandException(FileStatus.NotFound);
    private async Task<DocumentValidation> Validate(Context c, ExternalDocument d, string text, CancellationToken ct)
    {
        var format = DocumentValidator.Format(d.Path);
        var result = await Task.Run(() => DocumentValidator.Validate(format, text), ct);
        if (format != DocumentFormat.Txt)
            await Event(c, d, format == DocumentFormat.Json ? OperationType.JSON_VALIDATED : OperationType.XML_VALIDATED,
                "EXTERNAL_VALIDATED", result.IsValid ? OperationResult.Success : OperationResult.Failed, Guid.NewGuid(), ct);
        return result;
    }
    public Task<FileResult<TextDocument>> OpenAsync(string path, CancellationToken ct = default) => Run(async (c, token) =>
    {
        c.File = await Task.Run(() => storage.Open(path, false), token);
        var text = await Task.Run(c.File.Read, token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Path.ToUpperInvariant())));
        var record = await c.Work.FindPathAsync(key, token) ?? new ExternalDocument { Path = text.Path, PathKey = key };
        if (!string.Equals(record.Path, text.Path, StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Conflict);
        Update(record, text); await c.Work.TrackAsync(record, token);
        return await Document(c, record, text, token);
    }, ct);
    private async Task<TextDocument> Document(Context c, ExternalDocument record, ExternalText text, CancellationToken ct)
    {
        var validation = await Validate(c, record, text.Content, ct);
        if (validation.Prohibited) throw new FileCommandException(FileStatus.InvalidDocument, validation, DocumentValidator.Format(record.Path));
        await Event(c, record, OperationType.FILE_READ, "EXTERNAL_READ", OperationResult.Success, Guid.NewGuid(), ct);
        return new(record.Id, System.IO.Path.GetFileName(record.Path), text.Content, text.Version + "|" + Convert.ToBase64String(record.RowVersion)) { Validation = validation };
    }
    public Task<FileResult<TextDocument>> ReadAsync(Guid id, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token);
        c.File = await Task.Run(() => storage.Open(record.Path, false), token);
        var text = await Task.Run(c.File.Read, token);
        Update(record, text); await c.Work.TrackAsync(record, token);
        return await Document(c, record, text, token);
    }, ct);
    public Task<FileResult<TextDocument>> ReloadAsync(Guid id, CancellationToken ct = default) => ReadAsync(id, ct);
    public Task<FileResult<bool>> SaveAsync(Guid id, string text, string version, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token);
        if (!version.EndsWith("|" + Convert.ToBase64String(record.RowVersion), StringComparison.Ordinal)) throw new FileCommandException(FileStatus.Conflict);
        c.File = await Task.Run(() => storage.Open(record.Path, true), token);
        var validation = await Validate(c, record, text, token);
        if (!validation.IsValid) throw new FileCommandException(FileStatus.InvalidDocument, validation, DocumentValidator.Format(record.Path));
        Check(c);
        var correlation = Guid.NewGuid();
        await Task.Run(() => c.File.Prepare(correlation, text, version[..version.LastIndexOf('|')]), token);
        Check(c); token.ThrowIfCancellationRequested();
        await Task.Run(c.File.Publish, token);
        var current = await Task.Run(c.File.Read, token);
        Update(record, current); await c.Work.TrackAsync(record, token);
        await Event(c, record, OperationType.FILE_UPDATED, "EXTERNAL_SAVED", OperationResult.Success, correlation, token);
        return true;
    }, ct);
    public Task<FileResult<DocumentValidation>> ValidateAsync(Guid id, string text, CancellationToken ct = default) => Run(async (c, token) =>
    {
        var record = await Record(c, id, token);
        c.File = await Task.Run(() => storage.Open(record.Path, false), token);
        return await Validate(c, record, text, token);
    }, ct);
    public Task<FileResult<bool>> RecoverAsync(string path, CancellationToken ct = default) => Run(async (c, token) =>
    {
        c.File = await Task.Run(() => storage.Open(path, true, true), token);
        if (c.File.Pending is not { } correlation) return true;
        // Commit correlation is checked on a separate connection; recovery never guesses.
        var committed = await store.CommittedAsync(correlation, c.File.Path, token);
        await Task.Run(() => c.File.Settle(committed), token);
        var current = await Task.Run(c.File.Read, token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Path.ToUpperInvariant())));
        var record = await c.Work.FindPathAsync(key, token) ?? new ExternalDocument { Path = current.Path, PathKey = key };
        Update(record, current); await c.Work.TrackAsync(record, token);
        await Event(c, record, OperationType.FILE_UPDATED, "EXTERNAL_RECOVERED", OperationResult.Success, Guid.NewGuid(), token);
        return true;
    }, ct);
}
