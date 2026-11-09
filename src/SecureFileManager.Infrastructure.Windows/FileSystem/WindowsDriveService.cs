using System.IO;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
namespace SecureFileManager.Infrastructure.Windows.FileSystem;

public sealed class WindowsDriveService(IUserSession session, Func<DriveInfo[]>? enumerate = null) : IDriveService
{
    public async Task<FileResult<IReadOnlyList<DriveSnapshot>>> GetAsync(CancellationToken ct = default)
    {
        var revision = session.Revision;
        if (session.Current is null) return new(FileStatus.Denied);
        try
        {
            var rows = await Task.Run(() =>
            {
                var drives = (enumerate ?? DriveInfo.GetDrives)();
                return drives.Select(Read).ToArray();
            }, ct);
            return revision != session.Revision ? new(FileStatus.Denied) : new(FileStatus.Success, rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(FileStatus.Unavailable); }
    }
    private static DriveSnapshot Read(DriveInfo drive)
    {
        try
        {
            if (drive.IsReady)
                return new(drive.Name, drive.VolumeLabel, drive.DriveType.ToString(), drive.DriveFormat, true, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return new(drive.Name, "Носитель не готов / недоступен", "Недоступно", "—", false, 0, 0);
    }
}
