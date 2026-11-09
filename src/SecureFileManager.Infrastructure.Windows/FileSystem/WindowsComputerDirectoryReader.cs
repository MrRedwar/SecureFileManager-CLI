using System.IO;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed class WindowsComputerDirectoryReader(PersonalStorage personal) : IComputerDirectoryReader
{
    private readonly string _privateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(personal.StorageRoot));
    private bool Private(string path) => path.Equals(_privateRoot, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(_privateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal string Normalize(string path)
    {
        if (!Core.Security.ExternalPathSyntax.IsValid(path) || !Path.IsPathFullyQualified(path) ||
            path.Split('\\').Any(x => x.StartsWith(".sfm-", StringComparison.OrdinalIgnoreCase)))
            throw new FileCommandException(FileStatus.Invalid);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Private(full)) throw new FileCommandException(FileStatus.Denied);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new FileCommandException(FileStatus.Denied);
        return full;
    }

    public ComputerDirectory List(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (path == "")
        {
            var roots = new List<ComputerEntry>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                if (drive.DriveType == DriveType.Network) continue;
                var ready = false;
                try { ready = drive.IsReady; } catch (IOException) { }
                roots.Add(new(drive.Name, drive.Name, true, null, null, ready && !Private(drive.Name),
                    ready ? "Локальный диск" : "Носитель не готов"));
            }
            return new("", null, roots);
        }
        var full = Normalize(path);
        // Hold every physical ancestor: a junction/rename must not bypass the private root.
        var pins = SafeNativeFile.Pin(full);
        try
        {
            var entries = new List<ComputerEntry>();
            var truncated = false;
            foreach (var item in new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= FileRules.ListLimit) { truncated = true; break; }
                try
                {
                    if (item.Name.StartsWith(".sfm-", StringComparison.OrdinalIgnoreCase)) continue;
                    var attributes = item.Attributes;
                    var folder = (attributes & FileAttributes.Directory) != 0;
                    var link = (attributes & FileAttributes.ReparsePoint) != 0;
                    var restricted = Private(item.FullName);
                    // Metadata only; do not follow links, read content or inspect private descendants.
                    entries.Add(new(item.FullName, item.Name, folder,
                        folder || link || restricted ? null : ((FileInfo)item).Length,
                        link || restricted ? null : item.LastWriteTimeUtc, folder && !link && !restricted,
                        restricted ? "Личное хранилище: используйте «Мои файлы»" :
                        link ? "Ссылка: переход недоступен" : ""));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { entries.Add(new(item.FullName, item.Name, false, null, null, false, "Сведения недоступны")); }
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
            return new(full, full.Equals(root, StringComparison.OrdinalIgnoreCase) ? "" : Directory.GetParent(full)?.FullName,
                entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(), truncated);
        }
        finally { foreach (var pin in pins) pin.Dispose(); }
    }
}
