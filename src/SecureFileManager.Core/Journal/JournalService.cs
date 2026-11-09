using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Security;

namespace SecureFileManager.Core.Journal;

public sealed class JournalService(IUserSession session, IUserRepository users, IOperationRepository operations,
    AuditHealth health, IAuditProbe probe, IAuditService? audit = null) : IJournalService
{
    private async Task<RepositoryResult<JournalPage>> Deny(SessionIdentity? actor, CancellationToken ct)
    {
        var saved = audit is null || await audit.RecordAsync(actor, OperationType.ACCESS_DENIED, OperationResult.Denied, "JOURNAL_ACCESS_DENIED", ct);
        return RepositoryResult<JournalPage>.Fail(saved ? RepositoryStatus.Unauthorized : RepositoryStatus.InfrastructureError);
    }
    public async Task<RepositoryResult<JournalPage>> GetAsync(JournalQuery query, CancellationToken ct = default)
    {
        var actor = session.Current; var revision = session.Revision;
        if (actor is null) return await Deny(null, ct);
        if (!query.IsValid) return RepositoryResult<JournalPage>.Fail(RepositoryStatus.Validation);
        var user = await users.GetByIdAsync(actor.Id, ct);
        if (user.Status == RepositoryStatus.InfrastructureError)
        { health.Failed(); return RepositoryResult<JournalPage>.Fail(RepositoryStatus.InfrastructureError); }
        if (!user.IsSuccess || !user.Value!.IsActive || revision != session.Revision)
            return await Deny(user.IsSuccess ? actor : null, ct);
        var result = await operations.QueryAsync(actor.Id, query, ct);
        if (result.Status == RepositoryStatus.InfrastructureError) health.Failed();
        if (revision != session.Revision || session.Current?.Id != actor.Id)
            return await Deny(actor, ct);
        if (!result.IsSuccess) return result;
        // Do not deliver arbitrary historical Details to the UI. Persistence/recovery still sees original codes.
        return RepositoryResult<JournalPage>.Ok(new(result.Value!.Items.Select(o => new Operation
        {
            Id = o.Id, UserId = o.UserId, FileId = o.FileId, CorrelationId = o.CorrelationId,
            ActorNameSnapshot = actor.Username, OccurredAtUtc = o.OccurredAtUtc, OperationType = o.OperationType, Result = o.Result,
            TargetPathSnapshot = RelativePathSyntax.IsValid(o.TargetPathSnapshot) ? o.TargetPathSnapshot : null,
            ExternalDocumentId = o.ExternalDocumentId,
            ExternalPathSnapshot = (o.ExternalDocumentId is not null || Files.ExternalOperationRules.AuditCode(o.Details)) && ExternalPathSyntax.IsValid(o.ExternalPathSnapshot) ? o.ExternalPathSnapshot : null,
            Details = o.Details is "LOGIN_LOCKED" or "PERSONAL_STORAGE_PREPARED" or "METADATA_SYNCHRONIZED" or
                "RECOVERY_FINALIZED" or "RECOVERY_ROLLED_BACK" or "STORAGE_DIAGNOSED" or "STORAGE_DIAGNOSIS_PARTIAL" or
                "COMPUTER_DIRECTORY_LISTED" or "EXTERNAL_READ" or "EXTERNAL_SAVED" or "EXTERNAL_VALIDATED" or "EXTERNAL_RECOVERED" ||
                Files.ExternalOperationRules.AuditCode(o.Details) ? o.Details : null
        }).ToArray(), result.Value.HasMore));
    }
    public async Task CheckHealthAsync(CancellationToken ct = default)
    {
        var actor = session.Current; var revision = session.Revision;
        if (actor is null) { health.Reset(); return; }
        var user = await users.GetByIdAsync(actor.Id, ct);
        if (revision != session.Revision) return;
        if (!user.IsSuccess || !user.Value!.IsActive) { health.Failed(); return; }
        await health.ProbeAsync(probe, ct);
        if (revision != session.Revision) health.Reset();
    }
}
