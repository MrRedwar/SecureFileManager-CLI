using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Data.Repositories;

public abstract class RepositoryBase(IDbContextFactory<AppDbContext> factory)
{
    protected async Task<RepositoryResult<T>> ExecuteAsync<T>(
        Func<AppDbContext, Task<RepositoryResult<T>>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            return await action(context);
        }
        catch (PersistenceValidationException) { return RepositoryResult<T>.Fail(RepositoryStatus.Validation); }
        catch (DbUpdateConcurrencyException) { return RepositoryResult<T>.Fail(RepositoryStatus.Conflict); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { return RepositoryResult<T>.Fail(RepositoryStatus.Conflict); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 547 or 2628 or 8152 or 515 })
        { return RepositoryResult<T>.Fail(RepositoryStatus.Validation); }
        catch (DbUpdateException) { return RepositoryResult<T>.Fail(RepositoryStatus.InfrastructureError); }
        catch (SqlException) { return RepositoryResult<T>.Fail(RepositoryStatus.InfrastructureError); }
        catch (InvalidOperationException ex) when (ex.InnerException is SqlException)
        { return RepositoryResult<T>.Fail(RepositoryStatus.InfrastructureError); }
        // Cancellation is intentionally propagated, never reported as successful persistence.
    }
}
