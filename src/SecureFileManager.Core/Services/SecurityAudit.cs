using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
namespace SecureFileManager.Core.Services;

public sealed class SecurityAudit(IOperationRepository repository, IClock clock, Journal.IAuditHealth? health = null) : IAuditService
{
    public Task<bool> RecordAsync(SessionIdentity? actor, OperationType type, OperationResult result, string code, CancellationToken ct = default) =>
        RecordCoreAsync(actor, type, result, code, ct);
    private async Task<bool> RecordCoreAsync(SessionIdentity? actor, OperationType type, OperationResult result, string code, CancellationToken ct)
    {
        var saved = await repository.AddAsync(Create(clock.UtcNow, actor, type, result, code), ct);
        if (saved.IsSuccess) health?.Succeeded(); else health?.Failed();
        return saved.IsSuccess;
    }
    public static Operation Create(DateTime now, SessionIdentity? actor, OperationType type, OperationResult result, string code) => new()
    {
        UserId = actor?.Id, ActorNameSnapshot = actor?.Username, OccurredAtUtc = now,
        OperationType = type, Result = result, Details = code, FileId = null, TargetPathSnapshot = null
    };
    public static async Task<bool> InTransactionAsync(IIdentityTransaction tx, DateTime now, SessionIdentity? actor,
        OperationType type, OperationResult result, string code, CancellationToken ct, string? unknownName = null)
    {
        await tx.FlushAsync(ct);
        var operation = Create(now, actor, type, result, code);
        if (actor is null) operation.ActorNameSnapshot = unknownName;
        return (await tx.Audit.AddAsync(operation, ct)).IsSuccess;
    }
}
