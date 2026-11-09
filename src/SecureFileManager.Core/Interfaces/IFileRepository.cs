using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Interfaces;

// ownerId must come from the authenticated service session (stage 04), never a UI choice.
// This persistence contract performs no physical filesystem operations.
public interface IFileRepository
{
    Task<RepositoryResult<IReadOnlyList<Files.DiagnosticRecord>>> DiagnosticAsync(Guid ownerId, CancellationToken ct = default);
    Task<RepositoryResult<FileRecord>> AddAsync(FileRecord file, CancellationToken cancellationToken = default);
    Task<RepositoryResult<FileRecord>> GetByIdAsync(Guid ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<RepositoryResult<FileRecord>> GetByPathAsync(Guid ownerId, string relativePath, CancellationToken cancellationToken = default);
    Task<RepositoryResult<IReadOnlyList<FileRecord>>> ListAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<RepositoryResult<IReadOnlyList<FileRecord>>> ListChildrenAsync(Guid ownerId, string directory, CancellationToken cancellationToken = default);
    Task<RepositoryResult<FileRecord>> UpdateAsync(FileRecord file, CancellationToken cancellationToken = default);
    Task<RepositoryResult<FileRecord>> SoftDeleteAsync(Guid ownerId, Guid id, byte[] rowVersion,
        DateTime deletedAtUtc, CancellationToken cancellationToken = default);
}
