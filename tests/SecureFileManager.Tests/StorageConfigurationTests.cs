using System.IO;
using SecureFileManager.Infrastructure.Windows.FileSystem;
using SecureFileManager.Core.Results;
using System.Text.Json;

namespace SecureFileManager.Tests;

public sealed class StorageConfigurationTests
{
    private static string IsolatedDirectory()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("SFM_STORAGE_TEST_ROOT") ?? Path.GetTempPath(),
            "SecureFileManager-storage-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void ConfigurationUsesSelectedEnvironmentAndExplicitOverrideWithoutAppDataFallback()
    {
        var directory = IsolatedDirectory();
        var root = Path.Combine(directory, "Storage");
        File.WriteAllText(Path.Combine(directory, "appsettings.Development.json"), JsonSerializer.Serialize(new { StorageRoot = root }));
        Assert.Equal(root, StorageConfiguration.LoadRoot("Development", directory));
        Assert.Throws<InvalidOperationException>(() => StorageConfiguration.LoadRoot("Production", directory));
        var production = Path.Combine(directory, "ProductionStorage");
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), JsonSerializer.Serialize(new { StorageRoot = production }));
        Assert.Equal(production, StorageConfiguration.LoadRoot("Production", directory));
        var explicitRoot = Path.Combine(directory, "ExplicitStorage");
        Assert.Equal(explicitRoot, StorageConfiguration.LoadRoot("Development", directory, explicitRoot));
        Assert.Equal(root, StorageConfiguration.LoadRoot("Development", directory, " "));
        Assert.Throws<InvalidOperationException>(() => StorageConfiguration.LoadRoot("../Development", directory));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"StorageRoot\":\"\"}")]
    [InlineData("{\"StorageRoot\":42}")]
    [InlineData("{ broken JSON test-only }")]
    public void InvalidConfigurationFailsClosed(string json)
    {
        var directory = IsolatedDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.Development.json"), json);
        var error = Assert.Throws<InvalidOperationException>(() => StorageConfiguration.LoadRoot("Development", directory));
        Assert.Contains("StorageRoot", error.Message);
        Assert.DoesNotContain(json, error.Message);
    }

    [Fact]
    public async Task ConfiguredRootReusesExistingGuidFolderAndPreservesItsFiles()
    {
        var directory = IsolatedDirectory();
        var root = Path.Combine(directory, "Storage");
        File.WriteAllText(Path.Combine(directory, "appsettings.Development.json"), JsonSerializer.Serialize(new { StorageRoot = root }));
        var storage = new PersonalStorage(StorageConfiguration.LoadRoot("Development", directory));
        var first = Guid.NewGuid();
        var created = await storage.PrepareAsync(first);
        Assert.True(created.Succeeded); Assert.True(created.Created);
        Assert.Equal(Path.Combine(root, "Users", first.ToString("D")), created.PhysicalPath);
        var sentinel = Path.Combine(created.PhysicalPath!, "sentinel-test-only.txt");
        await File.WriteAllTextAsync(sentinel, "Preserve test-only contents");
        var reused = await storage.PrepareAsync(first);
        Assert.True(reused.Succeeded); Assert.False(reused.Created);
        Assert.Equal(created.PhysicalPath, reused.PhysicalPath);
        Assert.Equal("Preserve test-only contents", await File.ReadAllTextAsync(sentinel));
        var second = await storage.PrepareAsync(Guid.NewGuid());
        Assert.True(second.Succeeded); Assert.NotEqual(created.PhysicalPath, second.PhysicalPath);
        Assert.Equal(StorageStatus.InvalidPath, storage.Resolve(first, "../outside").Status);
        Assert.Equal(StorageStatus.InvalidPath, storage.Resolve(first, sentinel).Status);
        Assert.False(Directory.Exists(Path.Combine(root, "Storage")));
        Assert.False(Directory.Exists(Path.Combine(root, "Users", "Users")));
    }

    [Fact]
    public async Task UnavailableDriveDoesNotCreateAlternateStorage()
    {
        var used = DriveInfo.GetDrives().Select(x => x.Name[..1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(x => ((char)x).ToString()).Last(x => !used.Contains(x));
        var storage = new PersonalStorage(letter + @":\Sfm-test-only\Storage");
        var result = await storage.PrepareAsync(Guid.NewGuid());
        Assert.Equal(StorageStatus.Unavailable, result.Status);
        Assert.Null(result.PhysicalPath);
    }

    [Fact]
    public void StorageRootRejectsVolumeRootRelativeAndNetworkLocations()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidOperationException>(() => new PersonalStorage(volumeRoot));
        Assert.Throws<InvalidOperationException>(() => new PersonalStorage(Path.Combine(volumeRoot, "test", "..")));
        Assert.Throws<InvalidOperationException>(() => new PersonalStorage("Storage"));
        Assert.Throws<InvalidOperationException>(() => new PersonalStorage(@"\\localhost\share\Storage"));
    }
}
