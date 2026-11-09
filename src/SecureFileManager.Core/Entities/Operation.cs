namespace SecureFileManager.Core.Entities;

public enum OperationType
{
    LOGIN_SUCCESS, LOGIN_FAILED, LOGOUT, FILE_CREATED, FILE_READ, FILE_UPDATED,
    FILE_DELETED, DIRECTORY_CREATED, JSON_VALIDATED, XML_VALIDATED,
    ZIP_CREATED, ZIP_EXTRACTED, ACCESS_DENIED, OPERATION_FAILED
}

public enum OperationResult { Success, Denied, Failed }

public sealed class Operation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? ActorNameSnapshot { get; set; }
    public Guid? FileId { get; set; }
    public Guid? ExternalDocumentId { get; set; }
    public string? ExternalPathSnapshot { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public OperationType OperationType { get; set; }
    public OperationResult Result { get; set; }
    public string? TargetPathSnapshot { get; set; }
    public string? Details { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
}
