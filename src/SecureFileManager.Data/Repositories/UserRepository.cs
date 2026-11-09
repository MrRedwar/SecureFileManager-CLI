using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Data.Repositories;

public sealed class UserRepository(IDbContextFactory<AppDbContext> factory) : RepositoryBase(factory), IUserRepository
{
    public Task<RepositoryResult<User>> AddAsync(User user, CancellationToken cancellationToken = default) =>
        ExecuteAsync<User>(async db =>
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<User>.Ok(user);
        }, cancellationToken);

    public Task<RepositoryResult<User>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync<User>(async db => From(await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)), cancellationToken);

    public Task<RepositoryResult<User>> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        ExecuteAsync<User>(async db => From(await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Username == username, cancellationToken)), cancellationToken);

    private static RepositoryResult<User> From(User? user) => user is null
        ? RepositoryResult<User>.Fail(RepositoryStatus.NotFound) : RepositoryResult<User>.Ok(user);
}
