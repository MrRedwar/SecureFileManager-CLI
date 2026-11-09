using System.IO;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Services;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Infrastructure.Windows.FileSystem;

namespace SecureFileManager.Tests;
public sealed partial class FileServiceTests
{
    [SqlFact]
    public async Task ComputerBrowserSharesExistingExternalFilesWithoutChangingMetadataOrPersonalAccess()
    {
        var r = await New(); var other = await New();
        var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "shared-computer"); Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "already-existing.txt"); await File.WriteAllTextAsync(file, "preserve bytes");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        var reader = new WindowsComputerDirectoryReader(r.Paths);
        var browser = new ComputerBrowseService(r.Session, new UserRepository(r.Db), reader,
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock()));
        var first = await browser.ListAsync(outside); Ok(first);
        Assert.Contains(first.Value!.Entries, x => x.Name == "already-existing.txt" && x.SizeBytes > 0);
        Assert.Contains(first.Value.Entries, x => x.Name == "nested" && x.CanEnter);
        r.Session.Set(other.User); var second = await browser.ListAsync(outside); Ok(second);
        Assert.Equal(first.Value.Entries.Select(x => x.Path), second.Value!.Entries.Select(x => x.Path));
        Assert.Equal(FileStatus.Denied, (await browser.ListAsync(r.Root)).Status);
        Assert.Equal(FileStatus.Denied, (await browser.ListAsync(r.Physical(""))).Status);
        var parent = await browser.ListAsync(Path.GetDirectoryName(r.Root)!); Ok(parent);
        Assert.Contains(parent.Value!.Entries, x => x.Path == r.Root && !x.CanEnter);
        Assert.Equal("preserve bytes", await File.ReadAllTextAsync(file));
        await using var db = r.Db.CreateDbContext();
        Assert.False(await db.Files.IgnoreQueryFilters().AnyAsync(x => x.OwnerId == r.User || x.OwnerId == other.User));
        var events = await db.Operations.Where(x => x.Details == "COMPUTER_DIRECTORY_LISTED" && (x.UserId == r.User || x.UserId == other.User)).ToListAsync();
        Assert.Contains(events, x => x.UserId == r.User); Assert.Contains(events, x => x.UserId == other.User);
        Assert.All(events, x => { Assert.Null(x.FileId); Assert.Null(x.TargetPathSnapshot); Assert.Equal(OperationResult.Success, x.Result); });
    }
    [SqlFact]
    public async Task ComputerBrowserRejectsTraversalNamespacesAndJunctionIntoPrivateStorage()
    {
        var r = await New(); var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "shared-computer"); Directory.CreateDirectory(outside);
        var reader = new WindowsComputerDirectoryReader(r.Paths);
        var browser = new ComputerBrowseService(r.Session, new UserRepository(r.Db), reader,
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock()));
        foreach (var invalid in new[] { "..", "C:relative", @"C:\..\Windows", @"\\localhost\share", @"\\?\C:\Windows", @"C:\Windows:stream", @"C:\Windows.\" })
            Assert.Equal(FileStatus.Invalid, (await browser.ListAsync(invalid)).Status);
        var link = Path.Combine(outside, "private-link"); SecurityIntegrationTests.CreateJunction(link, r.Root);
        var listing = await browser.ListAsync(outside); Ok(listing);
        Assert.False(Assert.Single(listing.Value!.Entries, x => x.Name == "private-link").CanEnter);
        Assert.Equal(FileStatus.Denied, (await browser.ListAsync(link)).Status);
        r.Session.Set(null); Assert.Equal(FileStatus.Denied, (await browser.ListAsync(outside)).Status);
        Assert.True(Directory.Exists(r.Physical("")));
    }
}
