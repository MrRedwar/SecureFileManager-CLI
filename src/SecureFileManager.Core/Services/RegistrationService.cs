using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Security;
namespace SecureFileManager.Core.Services;

public sealed class RegistrationService(IIdentityStore store, IPasswordSecurity passwords,
    IPersonalStorageProvisioner storage, IClock clock, IAuditService audit) : IRegistrationService
{
    public async Task<AuthOutcome> RegisterAsync(string username, string password, string confirmation, CancellationToken ct = default)
    {
        if (!IdentityValidation.ValidUsername(username))
            return new(AuthStatus.Validation, "Логин: 3–50 символов, без пробелов и управляющих символов.");
        if (!IdentityValidation.ValidPassword(password))
            return new(AuthStatus.Validation, "Пароль должен содержать от 10 до 128 символов.");
        if (password != confirmation) return new(AuthStatus.Validation, "Пароли не совпадают.");
        var hash = await Task.Run(() => passwords.Hash(password), ct);
        password = string.Empty; confirmation = string.Empty;
        var id = Guid.NewGuid();
        var result = await store.ExecuteAsync(async tx =>
        {
            if (await tx.FindByUsernameAsync(username, ct) is not null)
                return new(new(AuthStatus.Conflict, "Этот логин уже занят."), false);
            var user = new User { Id = id, Username = username, PasswordHash = hash, CreatedAtUtc = clock.UtcNow };
            tx.Add(user);
            await tx.FlushAsync(ct); // staged SQL insert, still uncommitted
            var prepared = await storage.PrepareAsync(id, ct);
            if (!prepared.Succeeded) return new(new(AuthStatus.StorageUnavailable,
                "Не удалось подготовить личное хранилище. Учётная запись не сохранена. Проверьте доступ и повторите регистрацию."), false);
            var saved = await SecurityAudit.InTransactionAsync(tx, clock.UtcNow, new(id, username),
                OperationType.DIRECTORY_CREATED, OperationResult.Success, "PERSONAL_STORAGE_PREPARED", ct);
            return new(saved ? AuthOutcome.Ok("Регистрация завершена. Теперь войдите в приложение.") : AuthOutcome.Infrastructure(), saved);
        }, ct);
        if (result.Status == AuthStatus.StorageUnavailable && !await audit.RecordAsync(null, OperationType.OPERATION_FAILED,
            OperationResult.Failed, "REGISTRATION_STORAGE_FAILED", ct)) return AuthOutcome.Infrastructure();
        return result;
    }
}
