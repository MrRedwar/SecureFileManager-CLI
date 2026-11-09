using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SecureFileManager.Core.Files;
namespace SecureFileManager.Infrastructure.Windows.FileSystem;

internal static class SafeNativeFile
{
    internal const uint Read = 0x80000000, DeleteAccess = 0x10000, Attributes = 0x80;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        public readonly long Length => ((long)SizeHigh << 32) | SizeLow;
        public readonly string Identity => $"{Volume:X8}:{IndexHigh:X8}{IndexLow:X8}";
        public readonly DateTime CreatedUtc => DateTime.FromFileTimeUtc(((long)Created.dwHighDateTime << 32) | (uint)Created.dwLowDateTime);
        public readonly DateTime ModifiedUtc => DateTime.FromFileTimeUtc(((long)Modified.dwHighDateTime << 32) | (uint)Modified.dwLowDateTime);
        public readonly bool Directory => (Attributes & 0x10) != 0;
    }
    internal static SafeFileHandle Open(string path, bool directory, bool delete = false, bool write = false)
    {
        // FILE_LIST_DIRECTORY participates in share-access checks; attributes-only handles do not pin renames.
        var nativePath = @"\\?\" + Path.GetFullPath(path);
        var h = CreateFile(nativePath, (directory ? Attributes | 1u : Read) | (delete ? DeleteAccess : 0) | (write ? 0x40000000u : 0), directory && !delete ? 3u : 1u,
            IntPtr.Zero, 3, 0x00200000 | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); Fail(); }
        try
        {
            var info = Information(h);
            if ((info.Attributes & 0x400) != 0 || info.Directory != directory || (!directory && info.Links != 1))
                throw new FileCommandException(FileStatus.Denied);
            var final = new StringBuilder(4096);
            var length = GetFinalPathNameByHandle(h, final, final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) throw new FileCommandException(FileStatus.Denied);
            var normalized = final.ToString();
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
            if (!string.Equals(Path.TrimEndingDirectorySeparator(normalized), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                StringComparison.OrdinalIgnoreCase)) throw new FileCommandException(FileStatus.Denied);
            return h;
        }
        catch { h.Dispose(); throw; }
    }
    internal static Info Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) Fail();
        return info;
    }
    internal static void SetModifiedUtc(SafeFileHandle handle, DateTime utc)
    {
        var value = utc.ToFileTimeUtc();
        var time = new System.Runtime.InteropServices.ComTypes.FILETIME
        { dwLowDateTime = unchecked((int)value), dwHighDateTime = unchecked((int)(value >> 32)) };
        if (!SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref time)) Fail();
    }
    internal static string Fingerprint(SafeFileHandle handle)
    {
        var info = Information(handle);
        if (info.Directory) return info.Identity;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long offset = 0; int count;
        while ((count = RandomAccess.Read(handle, buffer, offset)) > 0) { hash.AppendData(buffer, 0, count); offset += count; }
        return info.Identity + ":" + info.Length + ":" + info.ModifiedUtc.Ticks + ":" + Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static void Rename(SafeFileHandle handle, string destination)
    {
        var bytes = Encoding.Unicode.GetBytes(@"\\?\" + Path.GetFullPath(destination));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        // FileNameLength excludes NUL; Win32 still requires a terminated WCHAR path.
        var buffer = Marshal.AllocHGlobal(nameOffset + bytes.Length + 2);
        try
        {
            Marshal.Copy(new byte[nameOffset + bytes.Length + 2], 0, buffer, nameOffset + bytes.Length + 2);
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + nameOffset, bytes.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, nameOffset + bytes.Length + 2)) Fail();
            var final = new StringBuilder(4096);
            if (GetFinalPathNameByHandle(handle, final, final.Capacity, 0) == 0 ||
                !string.Equals(final.ToString(), @"\\?\" + Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new FileCommandException(FileStatus.RecoveryRequired);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void Delete(SafeFileHandle handle)
    {
        var data = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(data, 1); if (!SetFileInformationByHandle(handle, 4, data, 1)) Fail(); }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void CreateDirectoryNew(string path)
    { if (!CreateDirectory(@"\\?\" + Path.GetFullPath(path), IntPtr.Zero)) Fail(); }
    internal static List<SafeFileHandle> Pin(string directory, IReadOnlyDictionary<string, SafeFileHandle>? held = null)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var full = Path.GetFullPath(directory); var cursor = Path.GetPathRoot(full)!;
            void Add(string path)
            {
                // An already verified exclusive handle is a stronger pin. Reopening it with
                // FILE_SHARE_DELETE omitted would conflict with our own DELETE access.
                if (held is not null && held.TryGetValue(path, out var existing) && !existing.IsClosed)
                { if (!Information(existing).Directory) throw new FileCommandException(FileStatus.Denied); return; }
                handles.Add(Open(path, true));
            }
            Add(cursor);
            foreach (var part in full[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            { cursor = Path.Combine(cursor, part); Add(cursor); }
            return handles;
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    private static void Fail()
    {
        var code = Marshal.GetLastWin32Error();
        var failure = new FileCommandException(code switch
        {
            2 or 3 => FileStatus.NotFound, 5 => FileStatus.Denied,
            32 or 33 or 80 or 145 or 183 => FileStatus.Conflict, _ => FileStatus.Unavailable
        });
        failure.Data["Win32Error"] = code; throw failure;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Info information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, int capacity, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr data, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, IntPtr creation, IntPtr access, ref System.Runtime.InteropServices.ComTypes.FILETIME modified);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
}
