using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Security;
namespace SecureFileManager.Core.Services;

public sealed class AuthenticationService(IIdentityStore store, IPasswordSecurity passwords, UserSession session, IClock clock,
    LoginProtectionPolicy? policy = null)
    : IAuthenticationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LoginProtectionPolicy _policy = policy ?? new();
    public async Task<AuthOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (session.Current is not null) return new(AuthStatus.Conflict, "Сначала завершите текущий сеанс.");
            SessionIdentity? identity = null;
            var result = await store.ExecuteAsync(async tx =>
            {
                var user = IdentityValidation.ValidUsername(username) ? await tx.FindByUsernameAsync(username, ct) : null;
                var now = clock.UtcNow;
                var validPasswordInput = IdentityValidation.ValidPassword(password);
                var check = PasswordCheck.Failed;
                var eligible = user is { IsActive: true } && !(user.LockoutUntilUtc > now);
                if (eligible && validPasswordInput)
                    check = await Task.Run(() => passwords.Verify(user!.PasswordHash, password), ct);
                else
                    await Task.Run(() => passwords.VerifyDummy(password is { Length: <= 128 } ? password : "invalid-input"), ct);
                var actor = user is null ? null : new SessionIdentity(user.Id, user.Username);
                if (!eligible || check == PasswordCheck.Failed)
                {
                    if (user is { IsActive: true } && !(user.LockoutUntilUtc > now))
                    {
                        IReadOnlyList<DateTime> previous = [];
                        if (user.LockoutUntilUtc is null && user.FailedLoginWindowStartUtc is not null)
                            previous = await tx.RecentCountedFailuresAsync(user.Id, now - _policy.ObservationWindow,
                                Math.Min(user.FailedLoginCount, _policy.MaximumFailures - 1), ct);
                        user.FailedLoginCount = previous.Count + 1;
                        user.FailedLoginWindowStartUtc = previous.Count == 0 ? now : previous.Min();
                        user.LockoutUntilUtc = user.FailedLoginCount >= _policy.MaximumFailures ? now + _policy.LockoutDuration : null;
                    }
                    var audit = await SecurityAudit.InTransactionAsync(tx, now, actor,
                        OperationType.LOGIN_FAILED, OperationResult.Failed,
                        user?.LockoutUntilUtc > now ? "LOGIN_LOCKED" : eligible ? "INVALID_PASSWORD_COUNTED" : "INVALID_CREDENTIALS", ct,
                        actor is null && IdentityValidation.ValidUsername(username) &&
                            username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.') ? username : null);
                    return new(audit ? AuthOutcome.Invalid() : AuthOutcome.Infrastructure(), audit);
                }
                user!.FailedLoginCount = 0;
                user.FailedLoginWindowStartUtc = null;
                user.LockoutUntilUtc = null;
                if (check == PasswordCheck.RehashNeeded)
                    user.PasswordHash = await Task.Run(() => passwords.Hash(password), ct);
                var saved = await SecurityAudit.InTransactionAsync(tx, now, actor,
                    OperationType.LOGIN_SUCCESS, OperationResult.Success, "LOGIN", ct);
                identity = actor;
                return new(saved ? AuthOutcome.Ok() : AuthOutcome.Infrastructure(), saved);
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (result.Succeeded && identity is not null) session.Start(identity);
            return result;
        }
        finally { _gate.Release(); }
    }
    public async Task<AuthOutcome> LogoutAsync(CancellationToken ct = default)
    {
        // Cancellation may stop the audit, never the local termination of an existing session.
        await _gate.WaitAsync();
        try
        {
            var actor = session.Current;
            if (actor is null) return AuthOutcome.Ok();
            try
            {
                return await store.ExecuteAsync(async tx =>
                {
                    var saved = await SecurityAudit.InTransactionAsync(tx, clock.UtcNow, actor,
                        OperationType.LOGOUT, OperationResult.Success, "LOGOUT", ct);
                    return new(saved ? AuthOutcome.Ok() : new(AuthStatus.InfrastructureError,
                        "Сеанс завершён. Запись выхода в журнал не подтверждена."), saved);
                }, ct);
            }
            finally { session.End(); } // fail closed even when SQL/audit/cancellation fails
        }
        finally { _gate.Release(); }
    }
}
