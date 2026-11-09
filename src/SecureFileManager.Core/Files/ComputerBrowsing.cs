using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Files;

// External entries have no personal OwnerId/FileId. Browsing never adopts them into Files.
public sealed record ComputerEntry(string Path, string Name, bool IsDirectory, long? SizeBytes,
    DateTime? ModifiedUtc, bool CanEnter, string Note = "");
public sealed record ComputerDirectory(string Path, string? ParentPath, IReadOnlyList<ComputerEntry> Entries, bool IsTruncated = false);

public interface IComputerDirectoryReader
{
    // Empty path denotes the computer's local drive list; this port cannot mutate files.
    ComputerDirectory List(string path, CancellationToken ct);
}
public interface IComputerBrowseService
{
    Task<FileResult<ComputerDirectory>> ListAsync(string path, CancellationToken ct = default);
}

public sealed class ComputerBrowseService(IUserSession session, IUserRepository users,
    IComputerDirectoryReader reader, IAuditService audit) : IComputerBrowseService
{
    public async Task<FileResult<ComputerDirectory>> ListAsync(string path, CancellationToken ct = default)
    {
        var actor = session.Current;
        var revision = session.Revision;
        bool SameSession() => actor is not null && session.Revision == revision && session.Current?.Id == actor.Id;
        async Task<FileResult<ComputerDirectory>> Failure(FileStatus status)
        {
            try
            {
                var saved = await audit.RecordAsync(actor,
                    status is FileStatus.Denied or FileStatus.Invalid ? OperationType.ACCESS_DENIED : OperationType.OPERATION_FAILED,
                    status is FileStatus.Denied or FileStatus.Invalid ? OperationResult.Denied : OperationResult.Failed,
                    "COMPUTER_BROWSE_" + status, CancellationToken.None);
                return new(saved ? status : FileStatus.DatabaseUnavailable);
            }
            catch { return new(FileStatus.DatabaseUnavailable); }
        }
        try
        {
            if (actor is null) return await Failure(FileStatus.Denied);
            var user = await users.GetByIdAsync(actor.Id, ct);
            if (user.Status == RepositoryStatus.InfrastructureError) return new(FileStatus.DatabaseUnavailable);
            if (!user.IsSuccess || !user.Value!.IsActive || !SameSession()) return await Failure(FileStatus.Denied);
            var directory = await Task.Run(() => reader.List(path, ct), ct);
            if (!SameSession()) return await Failure(FileStatus.Denied);
            // Do not expose the list unless its mandatory audit was actually saved.
            if (!await audit.RecordAsync(actor, OperationType.FILE_READ, OperationResult.Success,
                "COMPUTER_DIRECTORY_LISTED", ct)) return new(FileStatus.DatabaseUnavailable);
            if (!SameSession()) return new(FileStatus.Denied);
            return new(FileStatus.Success, directory);
        }
        catch (OperationCanceledException) { return new(FileStatus.Cancelled); }
        catch (FileCommandException ex) { return await Failure(ex.Status); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return await Failure(ex is UnauthorizedAccessException or System.Security.SecurityException ? FileStatus.Denied : FileStatus.Unavailable); }
    }
}
