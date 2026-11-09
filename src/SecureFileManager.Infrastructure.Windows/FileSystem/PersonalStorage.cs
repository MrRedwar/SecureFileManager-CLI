using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Security;

namespace SecureFileManager.Infrastructure.Windows.FileSystem;

// Local filesystem adapter. Identity/session/authorization decisions are in Core.
public sealed class PersonalStorage : IPersonalStorageProvisioner, IPathPolicy
{
    private readonly string _root;
    internal string StorageRoot => _root;
    public PersonalStorage(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Корень хранения должен быть абсолютным.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (_root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(_root)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Требуется локальное хранилище.");
        if (string.Equals(_root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_root)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Корень диска нельзя использовать как хранилище.");
    }
    public static PersonalStorage FromConfiguration() => new(StorageConfiguration.LoadRoot());

    public Task<StorageOutcome> PrepareAsync(Guid userId, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (userId == Guid.Empty) return new StorageOutcome(StorageStatus.Denied);
        var target = UserRoot(userId);
        try
        {
            if (!new DriveInfo(Path.GetPathRoot(_root)!).IsReady) return new(StorageStatus.Unavailable);
            CheckChain(target);
            var existed = Directory.Exists(target);
            Directory.CreateDirectory(target);
            CheckChain(target);
            if (!Directory.Exists(target)) return new(StorageStatus.Unavailable);
            return new StorageOutcome(StorageStatus.Success, target, !existed);
        }
        catch (UnsafePathException) { return new(StorageStatus.Denied); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(StorageStatus.Unavailable); }
    }, ct);

    public StorageOutcome Resolve(Guid userId, string relativePath)
    {
        if (userId == Guid.Empty || relativePath is null ||
            (relativePath.Length != 0 && !RelativePathSyntax.IsValid(relativePath))) return new(StorageStatus.InvalidPath);
        try
        {
            var root = UserRoot(userId);
            if (!Directory.Exists(root)) return new(StorageStatus.Unavailable);
            CheckChain(root);
            var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var back = Path.GetRelativePath(root, target);
            if (Path.IsPathRooted(back) || back == ".." || back.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return new(StorageStatus.Denied);
            CheckChain(target);
            if (File.Exists(target))
            {
                using var handle = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1)
                    return new(StorageStatus.Denied);
            }
            return new(StorageStatus.Success, target);
        }
        catch (UnsafePathException) { return new(StorageStatus.Denied); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(StorageStatus.Unavailable); }
    }

    private string UserRoot(Guid id) => Path.Combine(_root, "Users", id.ToString("D"));
    private static void CheckChain(string path)
    {
        var full = Path.GetFullPath(path);
        var cursor = Path.GetPathRoot(full)!;
        foreach (var part in full[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            try
            {
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnsafePathException();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private sealed class UnsafePathException : Exception;
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
