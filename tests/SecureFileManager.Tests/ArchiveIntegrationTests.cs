using System.IO;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Tests;

public sealed partial class FileServiceTests
{
    private static IArchiveService ZipService(Rig r) => (IArchiveService)r.Files;
    [SqlFact]
    public async Task ZipCreationPreviewExtractionPreservesTreeFilesMetadataAndAudit()
    {
        var r = await New();
        Ok(await r.Files.CreateAsync("", "Documents", true)); Ok(await r.Files.CreateAsync("Documents", "nested", true));
        Ok(await r.Files.CreateAsync("Documents", "empty", true));
        Ok(await r.Files.CreateAsync("Documents/nested", "report.txt", false));
        var file = await r.Entry("report.txt", "Documents/nested"); var text = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        Ok(await r.Files.SaveAsync(text.Id, "тест UTF-8\r\n", text.Version));
        var folder = await r.Entry("Documents");
        Ok(await ZipService(r).CreateArchiveAsync([folder.Id!.Value], "", "backup.zip"));
        var archive = await r.Entry("backup.zip"); var preview = await ZipService(r).PreviewArchiveAsync(archive.Id!.Value); Ok(preview);
        Assert.True(preview.Value!.CanExtract); Assert.Equal(4, preview.Value.Entries.Count);
        Assert.False(Directory.Exists(r.Physical("unpacked")));
        var diagnostic = r.Build(new DiagnosticStorage(r.Disk, output.WriteLine));
        Ok(await diagnostic.ExtractArchiveAsync(archive.Id.Value, "", "unpacked", preview.Value.Version));
        Assert.Equal("тест UTF-8\r\n", await File.ReadAllTextAsync(r.Physical("unpacked/Documents/nested/report.txt")));
        Assert.True(Directory.Exists(r.Physical("unpacked/Documents/empty")));
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(5, await db.Files.CountAsync(x => x.OwnerId == r.User && (x.RelativePath == "unpacked" || x.RelativePath.StartsWith("unpacked/"))));
        var operations = await db.Operations.Where(x => x.UserId == r.User).ToListAsync();
        Assert.Contains(operations, x => x.OperationType == OperationType.ZIP_CREATED && x.Result == OperationResult.Success);
        Assert.Contains(operations, x => x.OperationType == OperationType.ZIP_EXTRACTED && x.Result == OperationResult.Success);
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
    private sealed class DiagnosticStorage(IPhysicalFileStorage inner, Action<string> log) : IPhysicalFileStorage
    {
        public IFileWorkspace Open(Guid owner) => new DiagnosticWorkspace(inner.Open(owner), log);
    }
    private sealed class DiagnosticWorkspace(IFileWorkspace inner, Action<string> log) : IFileWorkspace
    {
        private T Observe<T>(string label, Func<T> work)
        {
            try { return work(); }
            catch (Exception error)
            {
                log(label + ": " + error);
                void Codes(Exception e) { if (e is FileCommandException f) log("Status=" + f.Status + "; Win32=" + f.Data["Win32Error"]); if (e.InnerException is { } cause) Codes(cause); }
                Codes(error); throw;
            }
        }
        public IReadOnlyList<PhysicalEntry> List(string path) => inner.List(path);
        public PhysicalEntry Inspect(string path) => Observe("Inspect", () => inner.Inspect(path));
        public PhysicalText Read(string path) => inner.Read(path);
        public BinaryDocument OpenBinary(string path, long limit) => inner.OpenBinary(path, limit);
        public IReadOnlyList<FileJournal> Pending() => inner.Pending();
        public FileJournal Stage(Guid id, ChangeKind kind, string path, string? text = null, string? expected = null, ImportSelection? source = null) => inner.Stage(id, kind, path, text, expected, source);
        public FileJournal StageGenerated(Guid id, string path, bool directory, Action<IFilePreparation> prepare, CancellationToken ct) => Observe("StageGenerated", () => inner.StageGenerated(id, path, directory, prepare, ct));
        public void Publish(FileJournal journal) => Observe("Publish", () => { inner.Publish(journal); return true; });
        public void Settle(FileJournal journal, bool committed) => Observe("Settle", () => { inner.Settle(journal, committed); return true; });
        public void Dispose() => inner.Dispose();
    }
    [SqlFact]
    public async Task ZipSingleMultipleFilesAndSelfInclusionAndExistingDestination()
    {
        var r = await New(); var ids = new List<Guid>();
        foreach (var name in new[] { "a.txt", "b.json", "c.xml", "..allowed.txt" })
        { Ok(await r.Files.CreateAsync("", name, false)); ids.Add((await r.Entry(name)).Id!.Value); }
        Ok(await r.Files.CreateAsync("", "source", true)); var folder = await r.Entry("source"); ids.Add(folder.Id!.Value);
        Ok(await ZipService(r).CreateArchiveAsync([ids[0]], "", "one.zip"));
        Ok(await ZipService(r).CreateArchiveAsync(ids, "", "many.zip"));
        var many = await r.Entry("many.zip"); var manyPreview = (await ZipService(r).PreviewArchiveAsync(many.Id!.Value)).Value!;
        Ok(await ZipService(r).ExtractArchiveAsync(many.Id.Value, "", "mixed", manyPreview.Version));
        Assert.True(File.Exists(r.Physical("mixed/..allowed.txt")));
        Assert.Equal(FileStatus.Conflict, (await ZipService(r).CreateArchiveAsync(ids, "", "many.zip")).Status);
        Assert.Equal(FileStatus.ArchiveUnsafe, (await ZipService(r).CreateArchiveAsync([folder.Id!.Value], "source", "self.zip")).Status);
        var archive = await r.Entry("one.zip"); var preview = (await ZipService(r).PreviewArchiveAsync(archive.Id!.Value)).Value!;
        Assert.Equal(FileStatus.Conflict, (await ZipService(r).ExtractArchiveAsync(archive.Id.Value, "", "source", preview.Version)).Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Physical("source")));
    }
    [SqlFact]
    public async Task ZipPublishIoFailureCompensatesTreeAndRetrySucceeds()
    {
        var r = await New(); await File.WriteAllBytesAsync(r.Physical("source.zip"), ZipEngineTests.Zip(("folder/file.txt", "unchanged")));
        Ok(await r.Files.SynchronizeAsync("source.zip")); var archive = await r.Entry("source.zip");
        var preview = (await ZipService(r).PreviewArchiveAsync(archive.Id!.Value)).Value!;
        var failed = r.Build(new FaultStorage(r.Disk, () => throw new IOException("test-only failure")));
        Assert.False((await failed.ExtractArchiveAsync(archive.Id.Value, "", "result", preview.Version)).Succeeded);
        Assert.False(Directory.Exists(r.Physical("result")));
        await using (var db = r.Db.CreateDbContext()) Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User && x.RelativePath.StartsWith("result")));
        using (var space = r.Disk.Open(r.User)) Assert.Empty(space.Pending());
        Ok(await ZipService(r).ExtractArchiveAsync(archive.Id.Value, "", "result", preview.Version));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(r.Physical("result/folder/file.txt")));
    }
    private async Task<(FileEntry Entry, ArchivePreview Preview)> AddZip(Rig r, params (string Name, string Text)[] entries)
    {
        var name = "input-" + Guid.NewGuid().ToString("N") + ".zip";
        await File.WriteAllBytesAsync(r.Physical(name), ZipEngineTests.Zip(entries));
        Ok(await r.Files.SynchronizeAsync(name)); var item = await r.Entry(name);
        var preview = await ZipService(r).PreviewArchiveAsync(item.Id!.Value); Ok(preview);
        return (item, preview.Value!);
    }
    [SqlFact]
    public async Task ZipAccessSessionTraversalAndForeignDestinationAreDeniedAndAudited()
    {
        var r = await New(); var zip = await AddZip(r, ("data.txt", "private"));
        Ok(await r.Files.CreateAsync("", "private-folder", true));
        var folder = await r.Entry("private-folder"); var other = Guid.NewGuid();
        await using (var db = r.Db.CreateDbContext())
        {
            db.Users.Add(new User { Id = other, Username = "zip-" + Guid.NewGuid().ToString("N"), PasswordHash = "test-only" }); await db.SaveChangesAsync();
        }
        await r.Paths.PrepareAsync(other); r.Session.Set(other);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).CreateArchiveAsync([folder.Id!.Value], "", "foreign.zip")).Status);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).PreviewArchiveAsync(zip.Entry.Id!.Value)).Status);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id.Value, "", "foreign", zip.Preview.Version)).Status);
        r.Session.Set(r.User);
        Assert.Equal(FileStatus.Invalid, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id.Value, "../other", "bad", zip.Preview.Version)).Status);
        Assert.Equal(FileStatus.Invalid, (await ZipService(r).CreateArchiveAsync([zip.Entry.Id.Value], r.Root, "bad.zip")).Status);
        Assert.Equal(FileStatus.NotFound, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id.Value, "not-owned", "bad", zip.Preview.Version)).Status);
        r.Session.Set(null);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).PreviewArchiveAsync(zip.Entry.Id.Value)).Status);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).CreateArchiveAsync([zip.Entry.Id.Value], "", "denied.zip")).Status);
        await using var inspect = r.Db.CreateDbContext();
        Assert.Contains(await inspect.Operations.Where(x => x.UserId == other).ToListAsync(), x => x.OperationType == OperationType.ACCESS_DENIED && x.Result == OperationResult.Denied && x.FileId == null);
        Assert.False(Directory.Exists(r.Physical("foreign")));
    }
    [SqlFact]
    public async Task UnsafeZipDoesNotPublishAnythingOrRegisterSuccess()
    {
        var r = await New(); var zip = await AddZip(r, ("../outside.txt", "secret marker"));
        Assert.False(zip.Preview.CanExtract);
        Assert.Equal(FileStatus.ArchiveUnsafe, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id!.Value, "", "rejected", zip.Preview.Version)).Status);
        Assert.False(Directory.Exists(r.Physical("rejected"))); Assert.False(File.Exists(r.Physical("outside.txt")));
        await using var db = r.Db.CreateDbContext();
        Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User && x.RelativePath.StartsWith("rejected")));
        var events = await db.Operations.Where(x => x.UserId == r.User).ToListAsync();
        Assert.DoesNotContain(events, x => x.OperationType == OperationType.ZIP_EXTRACTED && x.Result == OperationResult.Success);
        Assert.Contains(events, x => x.OperationType == OperationType.ACCESS_DENIED || x.OperationType == OperationType.OPERATION_FAILED);
        Assert.All(events, x => Assert.DoesNotContain("secret marker", x.Details ?? ""));
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
    [SqlFact]
    public async Task ExtractAuditFailureRollsBackAllMetadataAndPublishedTree()
    {
        var r = await New(); var zip = await AddZip(r, ("a/one.txt", "one"), ("b/two.txt", "two"));
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage07AuditFailure ON Operations AFTER INSERT AS BEGIN THROW 51007, 'Test-only audit failure', 1; END;");
        try
        {
            Assert.Equal(FileStatus.DatabaseUnavailable, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id!.Value, "", "rollback", zip.Preview.Version)).Status);
            Assert.Equal(FileStatus.DatabaseUnavailable, (await ZipService(r).CreateArchiveAsync([zip.Entry.Id.Value], "", "rollback.zip")).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage07AuditFailure;"); }
        Assert.False(Directory.Exists(r.Physical("rollback"))); Assert.False(File.Exists(r.Physical("rollback.zip")));
        Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User && x.RelativePath.StartsWith("rollback")));
        Assert.False(await db.Operations.AnyAsync(x => x.UserId == r.User && x.OperationType == OperationType.ZIP_EXTRACTED));
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
    [SqlFact]
    public async Task MissingSqlCannotCreateOrExtractArchive()
    {
        var r = await New(); var zip = await AddZip(r, ("x", "data"));
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_missing", ConnectTimeout = 2 };
        var missing = r.Build(r.Disk, new SecureFileManager.Data.Files.FileTransactionStore(new Factory(connection.ConnectionString)));
        Assert.Equal(FileStatus.DatabaseUnavailable, (await missing.CreateArchiveAsync([zip.Entry.Id!.Value], "", "no-db.zip")).Status);
        Assert.Equal(FileStatus.DatabaseUnavailable, (await missing.ExtractArchiveAsync(zip.Entry.Id.Value, "", "no-db", zip.Preview.Version)).Status);
        Assert.False(File.Exists(r.Physical("no-db.zip"))); Assert.False(Directory.Exists(r.Physical("no-db")));
    }
    private sealed class CallbackProgress(Action<ArchiveProgress> action) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => action(value); }
    [SqlFact]
    public async Task CancellationDuringExtractionCleansStageAndSessionChangePreventsPublication()
    {
        var r = await New(); var zip = await AddZip(r, ("a/one", "1"), ("b/two", "2"));
        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(_ => cancel.Cancel());
        Assert.Equal(FileStatus.Cancelled, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id!.Value, "", "cancelled", zip.Preview.Version, progress, cancel.Token)).Status);
        Assert.False(Directory.Exists(r.Physical("cancelled")));
        using (var space = r.Disk.Open(r.User)) Assert.Empty(space.Pending());
        var revoke = new CallbackProgress(_ => r.Session.Set(null));
        Assert.Equal(FileStatus.Denied, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id.Value, "", "revoked", zip.Preview.Version, revoke)).Status);
        Assert.False(Directory.Exists(r.Physical("revoked")));
    }
    [SqlFact]
    public async Task DeclaredLimitsAndCrcMismatchLeaveNoFinalTree()
    {
        var r = await New(); var zip = await AddZip(r, ("x", "12345"));
        var limited = r.Build(r.Disk, limits: new(EntryBytes: 4));
        Assert.Equal(FileStatus.ArchiveLimit, (await limited.ExtractArchiveAsync(zip.Entry.Id!.Value, "", "limited", zip.Preview.Version)).Status);
        var bytes = ZipEngineTests.Zip(("x", "original")); bytes[31] ^= 1;
        await File.WriteAllBytesAsync(r.Physical("crc.zip"), bytes); Ok(await r.Files.SynchronizeAsync("crc.zip"));
        var file = await r.Entry("crc.zip"); var preview = await ZipService(r).PreviewArchiveAsync(file.Id!.Value); Ok(preview);
        Assert.Equal(FileStatus.ArchiveInvalid, (await ZipService(r).ExtractArchiveAsync(file.Id.Value, "", "crc-result", preview.Value!.Version)).Status);
        Assert.False(Directory.Exists(r.Physical("crc-result"))); Assert.False(Directory.Exists(r.Physical("limited")));
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
    [SqlFact]
    public async Task ReparseDestinationAndSourceCannotEscapeOwnerRoot()
    {
        var r = await New(); var zip = await AddZip(r, ("outside.txt", "must not escape"));
        Ok(await r.Files.CreateAsync("", "destination", true));
        var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "preserved");
        Directory.Delete(r.Physical("destination")); SecurityIntegrationTests.CreateJunction(r.Physical("destination"), outside);
        Assert.Equal(FileStatus.Denied, (await ZipService(r).ExtractArchiveAsync(zip.Entry.Id!.Value, "destination", "result", zip.Preview.Version)).Status);
        Assert.False(Directory.Exists(Path.Combine(outside, "result"))); Assert.Equal("preserved", await File.ReadAllTextAsync(sentinel));
        var folder = await r.Entry("destination");
        Assert.Equal(FileStatus.Denied, (await ZipService(r).CreateArchiveAsync([folder.Id!.Value], "", "unsafe.zip")).Status);
        Assert.False(File.Exists(r.Physical("unsafe.zip")));
    }
    [SqlFact]
    public async Task UncommittedTreeRecoveryAndUnexpectedReplacementPreserveData()
    {
        var r = await New(); var bytes = ZipEngineTests.Zip(("nested/file", "stage"));
        void Prepare(string name)
        {
            using var space = r.Disk.Open(r.User);
            var journal = space.StageGenerated(Guid.NewGuid(), name, true, area =>
            { using var input = new MemoryStream(bytes); ZipEngine.Extract(input, area, new(), null, default); }, default);
            space.Publish(journal); // interrupted before SQL commit, only test-owned data
        }
        Prepare("interrupted"); Assert.Equal(FileStatus.RecoveryRequired, (await r.Files.ListAsync("")).Status);
        Ok(await r.Files.RecoverAsync()); Assert.False(Directory.Exists(r.Physical("interrupted")));
        Prepare("changed"); await File.WriteAllTextAsync(r.Physical("changed/nested/file"), "external replacement");
        Assert.Equal(FileStatus.RecoveryRequired, (await r.Files.RecoverAsync()).Status);
        Assert.Equal("external replacement", await File.ReadAllTextAsync(r.Physical("changed/nested/file")));
    }
    [SqlFact]
    public async Task AmbiguousZipCommitsRecoverWithoutDeletingCommittedTree()
    {
        var r = await New(); var zip = await AddZip(r, ("folder/file", "committed"));
        var uncertain = r.Build(r.Disk, new AmbiguousStore(new SecureFileManager.Data.Files.FileTransactionStore(r.Db), true));
        Assert.Equal(FileStatus.RecoveryRequired, (await uncertain.ExtractArchiveAsync(zip.Entry.Id!.Value, "", "committed", zip.Preview.Version)).Status);
        Assert.True(File.Exists(r.Physical("committed/folder/file"))); Ok(await r.Files.RecoverAsync());
        Assert.Equal("committed", await File.ReadAllTextAsync(r.Physical("committed/folder/file")));
        Assert.Equal(FileStatus.RecoveryRequired, (await uncertain.CreateArchiveAsync([zip.Entry.Id.Value], "", "committed.zip")).Status);
        Ok(await r.Files.RecoverAsync()); Assert.True(File.Exists(r.Physical("committed.zip")));
    }
    [SqlFact]
    public async Task ConcurrentArchiveNameOnlyPublishesOneAndLockedMissingInputsFailSafely()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "input.txt", false)); var item = await r.Entry("input.txt");
        var other = r.Build(r.Disk);
        var results = await Task.WhenAll(ZipService(r).CreateArchiveAsync([item.Id!.Value], "", "same.zip"), other.CreateArchiveAsync([item.Id.Value], "", "SAME.ZIP"));
        Assert.Single(results, x => x.Succeeded);
        using (var locked = new FileStream(r.Physical("input.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(FileStatus.Conflict, (await ZipService(r).CreateArchiveAsync([item.Id.Value], "", "locked.zip")).Status);
        File.Delete(r.Physical("input.txt"));
        Assert.Equal(FileStatus.NotFound, (await ZipService(r).CreateArchiveAsync([item.Id.Value], "", "missing.zip")).Status);
        Assert.False(File.Exists(r.Physical("locked.zip"))); Assert.False(File.Exists(r.Physical("missing.zip")));
    }
    [SqlFact]
    public async Task SimulatedDiskFullAfterPublishCompensatesArchiveAndPreservesSources()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "source.txt", false)); var item = await r.Entry("source.txt");
        var failed = r.Build(new FaultStorage(r.Disk, () => throw new IOException("test-only disk full", unchecked((int)0x80070070))));
        Assert.False((await failed.CreateArchiveAsync([item.Id!.Value], "", "disk-full.zip")).Succeeded);
        Assert.True(File.Exists(r.Physical("source.txt"))); Assert.False(File.Exists(r.Physical("disk-full.zip")));
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
}
