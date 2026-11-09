namespace SecureFileManager.Core.Files;

// Immutable limits are injectable at the service boundary; production defaults are never silently enlarged.
public sealed record ZipLimits(int Entries = 250, long EntryBytes = 25L * 1024 * 1024,
    long TotalBytes = 100L * 1024 * 1024, int Ratio = 100)
{
    public const long ArchiveBytes = 101L * 1024 * 1024;
    public void Check()
    {
        if (Entries is < 1 or > 250 || EntryBytes is < 1 or > 25L * 1024 * 1024 ||
            TotalBytes is < 1 or > 100L * 1024 * 1024 || Ratio is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(ZipLimits));
    }
}
public sealed record ArchiveProgress(int Completed, int Total, long Bytes);
public sealed record ArchiveItem(string Path, bool Directory, long Size, long CompressedSize, string Warning = "")
{
    public string SizeLabel => FileDisplay.Bytes(Size);
    public string CompressedLabel => FileDisplay.Bytes(CompressedSize);
}
public sealed record ArchivePreview(IReadOnlyList<ArchiveItem> Entries, string Version, string Warning = "")
{
    public bool CanExtract => Warning == "" && Entries.All(x => x.Warning == "");
    public int Files => Entries.Count(x => !x.Directory);
    public int Directories => Entries.Count(x => x.Directory);
    public long TotalBytes => Entries.Sum(x => x.Size);
    public string Summary => $"Файлов: {Files}; папок: {Directories}; заявлено: {FileDisplay.Bytes(TotalBytes)}. Размеры из ZIP; фактические байты проверяются при извлечении.";
}
public interface IArchiveService
{
    Task<FileResult<bool>> CreateArchiveAsync(IReadOnlyList<Guid> sources, string directory, string name,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default);
    Task<FileResult<ArchivePreview>> PreviewArchiveAsync(Guid id, CancellationToken ct = default);
    Task<FileResult<bool>> ExtractArchiveAsync(Guid id, string directory, string folderName, string expectedVersion,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default);
}
public sealed record BinaryDocument(Stream Stream, string Version) : IDisposable
{ public void Dispose() => Stream.Dispose(); }
public interface IFilePreparation
{
    // Empty path denotes the single generated file. Tree paths are validated relative paths.
    Stream CreateFile(string path);
    void CreateDirectory(string path);
}
public sealed record PreparedEntry(string Path, bool Directory, string Version);
public sealed record ArchiveSource(string Path, string EntryName, bool Directory);
