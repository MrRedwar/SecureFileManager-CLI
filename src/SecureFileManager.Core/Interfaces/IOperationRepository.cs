using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Interfaces;

public interface IOperationRepository
{
    Task<RepositoryResult<Journal.JournalPage>> QueryAsync(Guid userId, Journal.JournalQuery query, CancellationToken ct = default);
    Task<RepositoryResult<Operation>> AddAsync(Operation operation, CancellationToken cancellationToken = default);
    Task<RepositoryResult<IReadOnlyList<Operation>>> GetForUserAsync(Guid userId, int take = 100,
        CancellationToken cancellationToken = default);
}
