using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Interfaces;

public interface IUserRepository
{
    Task<RepositoryResult<User>> AddAsync(User user, CancellationToken cancellationToken = default);
    Task<RepositoryResult<User>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RepositoryResult<User>> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
}
