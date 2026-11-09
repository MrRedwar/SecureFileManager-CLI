using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
namespace SecureFileManager.Core.Services;

public sealed class PersonalFileAccessPolicy : IFileAccessPolicy
{
    public bool Allows(SessionIdentity? actor, Guid ownerId) => actor is not null && ownerId != Guid.Empty && actor.Id == ownerId;
}
public sealed class UserStorageService(IUserSession session, IFileAccessPolicy access, IPathPolicy paths,
    IPersonalStorageProvisioner provisioner, IFileRepository files, IAuditService audit, IUserRepository users) : IUserStorageService
{
    public async Task<StorageOutcome> PrepareCurrentAsync(CancellationToken ct = default)
    {
        var revision = session.Revision;
        var actor = session.Current;
        if (actor is null) return await DenyAsync(null, ct);
        var user = await users.GetByIdAsync(actor.Id, ct);
        if (user.Status == RepositoryStatus.InfrastructureError) return new(StorageStatus.InfrastructureError);
        if (!user.IsSuccess || !user.Value!.IsActive) return await DenyAsync(actor, ct);
        if (revision != session.Revision) return new(StorageStatus.Denied);
        var result = await provisioner.PrepareAsync(actor.Id, ct);
        if (revision != session.Revision) return new(StorageStatus.Denied);
        if (!result.Succeeded && !await audit.RecordAsync(actor, OperationType.OPERATION_FAILED,
            OperationResult.Failed, "STORAGE_PREPARE_FAILED", ct)) return new(StorageStatus.InfrastructureError);
        if (result.Succeeded && result.Created && !await audit.RecordAsync(actor, OperationType.DIRECTORY_CREATED,
            OperationResult.Success, "PERSONAL_STORAGE_REPAIRED", ct)) return new(StorageStatus.InfrastructureError);
        return result;
    }
    public async Task<StorageOutcome> CheckPathAsync(Guid ownerId, string relativePath, CancellationToken ct = default)
    {
        var revision = session.Revision;
        var actor = session.Current;
        if (!access.Allows(actor, ownerId)) return await DenyAsync(actor, ct);
        var user = await users.GetByIdAsync(actor!.Id, ct);
        if (user.Status == RepositoryStatus.InfrastructureError) return new(StorageStatus.InfrastructureError);
        if (!user.IsSuccess || !user.Value!.IsActive) return await DenyAsync(actor, ct);
        if (revision != session.Revision) return new(StorageStatus.Denied);
        var result = paths.Resolve(actor!.Id, relativePath);
        if (revision != session.Revision) return new(StorageStatus.Denied);
        if (result.Status is StorageStatus.Denied or StorageStatus.InvalidPath) return await DenyAsync(actor, ct);
        if (!result.Succeeded && !await audit.RecordAsync(actor, OperationType.OPERATION_FAILED,
            OperationResult.Failed, "STORAGE_UNAVAILABLE", ct)) return new(StorageStatus.InfrastructureError);
        return result;
    }
    public async Task<StorageOutcome> CheckFileAsync(Guid fileId, CancellationToken ct = default)
    {
        var actor = session.Current;
        if (actor is null) return await DenyAsync(null, ct);
        var revision = session.Revision;
        var file = await files.GetByIdAsync(actor.Id, fileId, ct);
        if (revision != session.Revision) return new(StorageStatus.Denied);
        if (!file.IsSuccess)
        {
            if (file.Status == RepositoryStatus.InfrastructureError) return new(StorageStatus.InfrastructureError);
            return await DenyAsync(actor, ct); // no foreign FileId in audit
        }
        return await CheckPathAsync(file.Value!.OwnerId, file.Value.RelativePath, ct);
    }
    private async Task<StorageOutcome> DenyAsync(SessionIdentity? actor, CancellationToken ct) =>
        new(await audit.RecordAsync(actor, OperationType.ACCESS_DENIED, OperationResult.Denied, "ACCESS_DENIED", ct)
            ? StorageStatus.Denied : StorageStatus.InfrastructureError);
}
