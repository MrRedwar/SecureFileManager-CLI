using System.IO;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Infrastructure.Windows.FileSystem;
namespace SecureFileManager.Tests;
public sealed class DriveTests
{
    private sealed class Session : IUserSession
    {
        public SessionIdentity? Current { get; } = new(Guid.NewGuid(), "test-only");
        public long Revision => 1;
        public event EventHandler? Changed { add { } remove { } }
    }
    [Fact]
    public async Task RealDriveDescriptionsAreBoundedAndNonReadyDriveDoesNotThrow()
    {
        var result = await new WindowsDriveService(new Session()).GetAsync();
        Assert.True(result.Succeeded);
        foreach (var drive in result.Value!)
        { Assert.InRange(drive.Percent, 0, 100); Assert.True(drive.Total >= 0); Assert.True(drive.Free >= 0); }
        var used = DriveInfo.GetDrives().Select(x => x.Name[..1]).ToHashSet();
        var letter = Enumerable.Range('D', 23).Select(x => ((char)x).ToString()).Last(x => !used.Contains(x));
        var absent = await new WindowsDriveService(new Session(), () => [new DriveInfo(letter)]).GetAsync();
        Assert.True(absent.Succeeded); Assert.False(Assert.Single(absent.Value!).IsReady);
        var failed = await new WindowsDriveService(new Session(), () => throw new IOException()).GetAsync();
        Assert.Equal(FileStatus.Unavailable, failed.Status);
        var empty = await new WindowsDriveService(new Session(), () => []).GetAsync();
        Assert.Empty(empty.Value!);
    }
}
