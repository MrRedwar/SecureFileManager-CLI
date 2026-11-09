using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;
namespace SecureFileManager.Core.Interfaces;

public sealed record SessionIdentity(Guid Id, string Username);
public interface IUserSession
{
    SessionIdentity? Current { get; }
    long Revision { get; }
    event EventHandler? Changed;
}
public interface IClock { DateTime UtcNow { get; } }
public interface IDatabaseConnectionCheck
{
    Task<bool> CanConnectAsync(CancellationToken ct = default);
}
public interface IPasswordSecurity
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
    void VerifyDummy(string password);
}
public enum PasswordCheck { Failed, Success, RehashNeeded }
public interface IAuthenticationService
{
    Task<AuthOutcome> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<AuthOutcome> LogoutAsync(CancellationToken ct = default);
}
public interface IRegistrationService
{
    Task<AuthOutcome> RegisterAsync(string username, string password, string confirmation, CancellationToken ct = default);
}
public interface IIdentityTransaction
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct);
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DateTime>> RecentCountedFailuresAsync(Guid id, DateTime since, int take, CancellationToken ct);
    void Add(User user);
    Task FlushAsync(CancellationToken ct);
    IOperationRepository Audit { get; }
}
public interface IIdentityStore
{
    Task<AuthOutcome> ExecuteAsync(Func<IIdentityTransaction, Task<IdentityDecision>> action, CancellationToken ct);
}
public sealed record IdentityDecision(AuthOutcome Outcome, bool Commit);
public interface IAuditService
{
    Task<bool> RecordAsync(SessionIdentity? actor, OperationType type, OperationResult result, string code, CancellationToken ct = default);
}
public interface IPersonalStorageProvisioner
{
    // Infrastructure port for trusted registration/workspace workflows, never a VM command.
    Task<StorageOutcome> PrepareAsync(Guid userId, CancellationToken ct = default);
}
public interface IPathPolicy
{
    StorageOutcome Resolve(Guid userId, string relativePath);
}
public interface IFileAccessPolicy { bool Allows(SessionIdentity? actor, Guid ownerId); }
public interface IUserStorageService
{
    Task<StorageOutcome> PrepareCurrentAsync(CancellationToken ct = default);
    Task<StorageOutcome> CheckPathAsync(Guid ownerId, string relativePath, CancellationToken ct = default);
    Task<StorageOutcome> CheckFileAsync(Guid fileId, CancellationToken ct = default);
}
