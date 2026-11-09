namespace SecureFileManager.Core.Results;

public enum RepositoryStatus { Success, Validation, Unauthorized, NotFound, Conflict, InfrastructureError }

// Contains no database exception, connection string, hash, or user-supplied error text.
public sealed record RepositoryResult<T>(RepositoryStatus Status, T? Value = default)
{
    public bool IsSuccess => Status == RepositoryStatus.Success;
    public static RepositoryResult<T> Ok(T value) => new(RepositoryStatus.Success, value);
    public static RepositoryResult<T> Fail(RepositoryStatus status) => new(status);
}
