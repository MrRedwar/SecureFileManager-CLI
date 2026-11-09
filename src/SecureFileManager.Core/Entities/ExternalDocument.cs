namespace SecureFileManager.Core.Entities;

public sealed class ExternalDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string PathKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public string Version { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}
