using SecureFileManager.Core.Entities;

namespace SecureFileManager.Core.Files;

public interface ITextDocumentService
{
    Task<FileResult<TextDocument>> ReadAsync(Guid id, CancellationToken ct = default);
    Task<FileResult<TextDocument>> ReloadAsync(Guid id, CancellationToken ct = default);
    Task<FileResult<bool>> SaveAsync(Guid id, string text, string version, CancellationToken ct = default);
    Task<FileResult<DocumentValidation>> ValidateAsync(Guid id, string text, CancellationToken ct = default);
}
public interface IExternalDocumentService : ITextDocumentService
{
    Task<FileResult<TextDocument>> OpenAsync(string path, CancellationToken ct = default);
    Task<FileResult<bool>> RecoverAsync(string path, CancellationToken ct = default);
}
public interface IExternalDocumentStore
{
    Task<T> ExecuteAsync<T>(Guid actor, Func<IExternalDocumentWork, Task<T>> action, CancellationToken ct);
    Task<bool> CommittedAsync(Guid correlation, string path, CancellationToken ct);
}
public interface IExternalDocumentWork
{
    Task<ExternalDocument?> FindAsync(Guid id, CancellationToken ct);
    Task<ExternalDocument?> FindPathAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<ExternalDocument>> FindTreeAsync(string path, CancellationToken ct);
    Task TrackAsync(ExternalDocument document, CancellationToken ct);
    Task AuditAsync(Operation operation, CancellationToken ct);
}
public sealed record ExternalText(string Path, string Content, string Version, long SizeBytes, DateTime ModifiedUtc);
public interface IExternalDocumentStorage
{
    IExternalDocumentFile Open(string path, bool writable, bool recovery = false);
}
public interface IExternalDocumentFile : IDisposable
{
    string Path { get; }
    ExternalText Read();
    Guid? Pending { get; }
    void Prepare(Guid correlation, string text, string expectedVersion);
    void Publish();
    void Settle(bool committed);
}
