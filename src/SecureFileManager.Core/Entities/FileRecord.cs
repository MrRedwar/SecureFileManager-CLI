namespace SecureFileManager.Core.Entities;

public enum FileKind { File, Directory }

public sealed class FileRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public FileKind Kind { get; set; }
    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
