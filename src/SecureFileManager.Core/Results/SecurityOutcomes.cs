namespace SecureFileManager.Core.Results;

public enum AuthStatus { Success, Validation, InvalidCredentials, Conflict, InfrastructureError, StorageUnavailable }
public sealed record AuthOutcome(AuthStatus Status, string Message)
{
    public bool Succeeded => Status == AuthStatus.Success;
    public static AuthOutcome Ok(string message = "") => new(AuthStatus.Success, message);
    public static AuthOutcome Infrastructure() => new(AuthStatus.InfrastructureError,
        "Не удалось подтвердить операцию в базе данных. Вход запрещён. Проверьте подключение и повторите попытку.");
    public static AuthOutcome Invalid() => new(AuthStatus.InvalidCredentials, "Неверный логин или пароль");
}
public enum StorageStatus { Success, Denied, InvalidPath, Unavailable, InfrastructureError }
public sealed record StorageOutcome(StorageStatus Status, string? PhysicalPath = null, bool Created = false)
{
    public bool Succeeded => Status == StorageStatus.Success;
}
