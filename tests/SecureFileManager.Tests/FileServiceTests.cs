using System.IO;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Services;
using SecureFileManager.Data;
using SecureFileManager.Data.Files;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Infrastructure.Windows.FileSystem;

using Xunit.Abstractions;

namespace SecureFileManager.Tests;

[Collection("SQL Server")]
public sealed partial class FileServiceTests(SqlDatabaseFixture database, ITestOutputHelper output)
{
    private sealed class Factory(string connection) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options); }
    private sealed class Session : IUserSession
    {
        public SessionIdentity? Current { get; private set; }
        public long Revision { get; private set; }
        public event EventHandler? Changed;
        public void Set(Guid? id, string name = "test-only") { Current = id is null ? null : new(id.Value, name); Revision++; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private sealed class Rig
    {
        public Factory Db { get; }
        public Session Session { get; } = new();
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("SFM_STORAGE_TEST_ROOT") ?? Path.GetTempPath(), "stage05-" + Guid.NewGuid().ToString("N"), "Storage");
        public Guid User { get; } = Guid.NewGuid();
        public PersonalStorage Paths { get; }
        public IPhysicalFileStorage Disk { get; }
        public IFileService Files { get; private set; }
        public Rig(string connection)
        {
            Db = new(connection); Paths = new(Root); Disk = new WindowsFileStorage(Paths);
            Files = Build(Disk);
        }
        public FileService Build(IPhysicalFileStorage disk, IFileTransactionStore? store = null, ZipLimits? limits = null) => new(Session, new PersonalFileAccessPolicy(),
            store ?? new FileTransactionStore(Db), disk, new SecurityAudit(new OperationRepository(Db), new SystemClock()), new SystemClock(), limits);
        public async Task Initialize()
        {
            await using var db = Db.CreateDbContext();
            db.Users.Add(new User { Id = User, Username = "f-" + Guid.NewGuid().ToString("N"), PasswordHash = "test-only-noncredential" });
            await db.SaveChangesAsync();
            Assert.True((await Paths.PrepareAsync(User)).Succeeded); Session.Set(User);
        }
        public string Physical(string path) => Path.Combine(Root, "Users", User.ToString("D"), path.Replace('/', Path.DirectorySeparatorChar));
        public async Task<FileEntry> Entry(string name, string folder = "") => Assert.Single((await Files.ListAsync(folder)).Value!, x => x.Name == name);
    }
    private async Task<Rig> New()
    { output.WriteLine("Stage05 isolated database: " + database.DatabaseName); var rig = new Rig(database.ConnectionString); await rig.Initialize(); return rig; }
    private static void Ok<T>(FileResult<T> result) => Assert.True(result.Succeeded, result.Status + ": " + result.Message);

    [SqlFact]
    public async Task EmptyListAndCompleteFileFolderCrudPersistMetadataAndAudit()
    {
        var r = await New(); Assert.Empty((await r.Files.ListAsync("")).Value!);
        Ok(await r.Files.CreateAsync("", "Документы", true));
        Ok(await r.Files.CreateAsync("Документы", "note.txt", false));
        var file = await r.Entry("note.txt", "Документы");
        var read = await r.Files.ReadAsync(file.Id!.Value); Ok(read); Assert.Empty(read.Value!.Content);
        Ok(await r.Files.SaveAsync(file.Id.Value, "Содержимое UTF-8", read.Value.Version));
        Assert.Equal("Содержимое UTF-8", await File.ReadAllTextAsync(r.Physical("Документы/note.txt")));
        var folder = await r.Entry("Документы");
        Assert.Equal(FileStatus.Conflict, (await r.Files.DeleteAsync(folder.Id!.Value)).Status);
        Ok(await r.Files.DeleteAsync(file.Id.Value)); Assert.False(File.Exists(r.Physical("Документы/note.txt")));
        Ok(await r.Files.DeleteAsync(folder.Id.Value)); Assert.False(Directory.Exists(r.Physical("Документы")));
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(2, await db.Files.IgnoreQueryFilters().CountAsync(x => x.OwnerId == r.User && x.IsDeleted));
        var events = await db.Operations.Where(x => x.UserId == r.User).ToListAsync();
        Assert.Contains(events, x => x.OperationType == OperationType.FILE_CREATED && x.Result == OperationResult.Success);
        Assert.Contains(events, x => x.OperationType == OperationType.FILE_UPDATED && x.Details == "FILE_TX_SAVE");
        Assert.Contains(events, x => x.OperationType == OperationType.FILE_DELETED && x.FileId == file.Id);
        using var workspace = r.Disk.Open(r.User); Assert.Empty(workspace.Pending());
    }
    [SqlFact]
    public async Task DuplicateAndConcurrentCaseInsensitiveCreateNeverOverwrite()
    {
        var r = await New();
        var other = r.Build(r.Disk);
        var results = await Task.WhenAll(r.Files.CreateAsync("", "same.txt", false), other.CreateAsync("", "SAME.TXT", false));
        Assert.Single(results, x => x.Succeeded);
        var path = Directory.EnumerateFiles(r.Physical("")).Single();
        await File.WriteAllTextAsync(path, "preserved external content");
        Assert.Equal(FileStatus.Conflict, (await r.Files.CreateAsync("", "Same.txt", false)).Status);
        Assert.Equal("preserved external content", await File.ReadAllTextAsync(path));
    }
    [SqlFact]
    public async Task InvalidNamesTraversalAndRootDeletionAreRejected()
    {
        var r = await New();
        foreach (var name in new[] { "", "..", "../bad.txt", @"C:\bad.txt", "NUL.txt", "COM¹.txt", "CONOUT$", "end.", "end ", "bad:ads.txt", ".sfm-internal", new string('x', 256) })
            Assert.Equal(FileStatus.Invalid, (await r.Files.CreateAsync("", name, false)).Status);
        Assert.Equal(FileStatus.Invalid, (await r.Files.ListAsync("../")).Status);
        Assert.Equal(FileStatus.Invalid, (await r.Files.ListAsync(r.Root)).Status);
        Assert.Equal(FileStatus.Invalid, (await r.Files.SynchronizeAsync("")).Status);
        Assert.True(Directory.Exists(r.Physical("")));
    }
    [SqlFact]
    public async Task ForeignIdsAndSessionChangeCannotReadWriteDeleteOrLeakResults()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "private.txt", false));
        var file = await r.Entry("private.txt"); var doc = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        var foreign = Guid.NewGuid();
        await using (var db = r.Db.CreateDbContext())
        { db.Users.Add(new User { Id = foreign, Username = "f-" + Guid.NewGuid().ToString("N"), PasswordHash = "test-only" }); await db.SaveChangesAsync(); }
        await r.Paths.PrepareAsync(foreign); r.Session.Set(foreign);
        Assert.Equal(FileStatus.Denied, (await r.Files.ReadAsync(file.Id.Value)).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.SaveAsync(file.Id.Value, "bad", doc.Version)).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.DeleteAsync(file.Id.Value)).Status);
        Assert.Empty((await r.Files.ListAsync("")).Value!);
        Assert.Equal(FileStatus.NotFound, (await r.Files.ListAsync("not-mine")).Status);
        r.Session.Set(null);
        Assert.Equal(FileStatus.Denied, (await r.Files.CreateAsync("", "denied.txt", false)).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.ListAsync("")).Status);
        Assert.Empty(await File.ReadAllTextAsync(r.Physical("private.txt")));
        r.Session.Set(r.User);
        var ending = r.Build(r.Disk, new SessionEndingStore(new FileTransactionStore(r.Db), () => r.Session.Set(null)));
        var stale = await ending.ReadAsync(file.Id.Value);
        Assert.Equal(FileStatus.Denied, stale.Status); Assert.Null(stale.Value);
    }
    [SqlFact]
    public async Task StaleEditorAndLockedFilesCannotDamageOriginal()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "note.txt", false));
        var item = await r.Entry("note.txt"); var first = (await r.Files.ReadAsync(item.Id!.Value)).Value!;
        Ok(await r.Files.SaveAsync(item.Id.Value, "first change", first.Version));
        Assert.Equal(FileStatus.Conflict, (await r.Files.SaveAsync(item.Id.Value, "stale change", first.Version)).Status);
        using (var locked = new FileStream(r.Physical("note.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(FileStatus.Conflict, (await r.Files.ReadAsync(item.Id.Value)).Status);
        Assert.Equal("first change", await File.ReadAllTextAsync(r.Physical("note.txt")));
    }
    [SqlFact]
    public async Task UntrackedChangedAndMissingRequireExplicitSynchronization()
    {
        var r = await New(); await File.WriteAllTextAsync(r.Physical("external.txt"), "external");
        var untracked = await r.Entry("external.txt");
        Assert.Equal(EntryState.Untracked, untracked.State); Assert.Null(untracked.Id);
        await using (var db = r.Db.CreateDbContext()) Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User));
        Ok(await r.Files.SynchronizeAsync("external.txt"));
        var tracked = await r.Entry("external.txt"); Assert.Equal(EntryState.Managed, tracked.State);
        await File.WriteAllTextAsync(r.Physical("external.txt"), "changed outside app");
        Assert.Equal(EntryState.Changed, (await r.Entry("external.txt")).State);
        Assert.Equal(FileStatus.Conflict, (await r.Files.ReadAsync(tracked.Id!.Value)).Status);
        Ok(await r.Files.SynchronizeAsync("external.txt"));
        Ok(await r.Files.ReadAsync(tracked.Id.Value));
        File.Delete(r.Physical("external.txt")); // owned test-only file
        Assert.Equal(EntryState.Missing, (await r.Entry("external.txt")).State);
        Ok(await r.Files.SynchronizeAsync("external.txt"));
        Assert.Empty((await r.Files.ListAsync("")).Value!);
    }
    [SqlFact]
    public async Task ImportCopiesSourceAndRejectsConflictsAndForeignStorageSources()
    {
        var r = await New();
        var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "Source"); Directory.CreateDirectory(outside);
        var source = Path.Combine(outside, "report.txt"); await File.WriteAllTextAsync(source, "unchanged source");
        Ok(await r.Files.ImportAsync("", new(source, "report.txt")));
        Assert.Equal("unchanged source", await File.ReadAllTextAsync(source));
        Assert.Equal("unchanged source", await File.ReadAllTextAsync(r.Physical("report.txt")));
        Assert.Equal(FileStatus.Conflict, (await r.Files.ImportAsync("", new(source, "report.txt"))).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.ImportAsync("", new(r.Physical("report.txt"), "copy.txt"))).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.ImportAsync("", new(source, "shortcut.lnk"))).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.ImportAsync("", new(@"\\server\share\file", "remote.txt"))).Status);
        Assert.Equal("unchanged source", await File.ReadAllTextAsync(source));
    }
    [SqlFact]
    public async Task ImportFailureCleansPartialStagingAndDoesNotTouchSource()
    {
        var r = await New();
        var source = Path.Combine(Path.GetDirectoryName(r.Root)!, "source.txt"); await File.WriteAllTextAsync(source, "source");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(FileStatus.Conflict, (await r.Files.ImportAsync("", new(source, "copy.txt"))).Status);
        Assert.False(File.Exists(r.Physical("copy.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(source));
        using var workspace = r.Disk.Open(r.User); Assert.Empty(workspace.Pending());
    }
    [SqlFact]
    public async Task TextEncodingAndSizeLimitsAreEnforcedBeforeReplacingOriginal()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "text.txt", false));
        var file = await r.Entry("text.txt"); var doc = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        Assert.Equal(FileStatus.LimitExceeded, (await r.Files.SaveAsync(file.Id.Value, new string('x', FileRules.TextLimit + 1), doc.Version)).Status);
        Assert.Empty(await File.ReadAllTextAsync(r.Physical("text.txt")));
        await File.WriteAllBytesAsync(r.Physical("text.txt"), [0xff, 0xfe, 0x80]);
        Ok(await r.Files.SynchronizeAsync("text.txt"));
        Assert.Equal(FileStatus.Unsupported, (await r.Files.ReadAsync(file.Id.Value)).Status);
        using (var oversized = new FileStream(r.Physical("text.txt"), FileMode.Create)) oversized.SetLength(FileRules.TextLimit + 1);
        Ok(await r.Files.SynchronizeAsync("text.txt"));
        Assert.Equal(FileStatus.LimitExceeded, (await r.Files.ReadAsync(file.Id.Value)).Status);
        var largeSource = Path.Combine(Path.GetDirectoryName(r.Root)!, "large.bin");
        using (var large = new FileStream(largeSource, FileMode.CreateNew)) large.SetLength(FileRules.ImportLimit + 1);
        Assert.Equal(FileStatus.LimitExceeded, (await r.Files.ImportAsync("", new(largeSource, "large.bin"))).Status);
        Assert.False(File.Exists(r.Physical("large.bin")));
    }
    [SqlFact]
    public async Task AuditFailureRollsBackPhysicalSaveAndCreationAndKeepsHistory()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "original.txt", false));
        var file = await r.Entry("original.txt"); var doc = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage05AuditFailure ON Operations AFTER INSERT AS BEGIN THROW 51005, 'Test-only audit failure', 1; END;");
        try
        {
            Assert.Equal(FileStatus.DatabaseUnavailable, (await r.Files.SaveAsync(file.Id.Value, "must roll back", doc.Version)).Status);
            Assert.Equal(FileStatus.DatabaseUnavailable, (await r.Files.CreateAsync("", "rollback.txt", false)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage05AuditFailure;"); }
        Assert.Empty(await File.ReadAllTextAsync(r.Physical("original.txt")));
        Assert.False(File.Exists(r.Physical("rollback.txt")));
        Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User && x.Name == "rollback.txt"));
        using var workspace = r.Disk.Open(r.User); Assert.Empty(workspace.Pending());
    }
    [SqlFact]
    public async Task FilesystemFailureAfterPublishIsCompensatedAndSessionRevocationIsChecked()
    {
        var r = await New();
        var failed = r.Build(new FaultStorage(r.Disk, () => throw new IOException("test-only simulated disk failure")));
        Assert.False((await failed.CreateAsync("", "failed.txt", false)).Succeeded);
        Assert.False(File.Exists(r.Physical("failed.txt")));
        var revoked = r.Build(new FaultStorage(r.Disk, () => r.Session.Set(null)));
        Assert.Equal(FileStatus.Denied, (await revoked.CreateAsync("", "revoked.txt", false)).Status);
        Assert.False(File.Exists(r.Physical("revoked.txt")));
        using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
    }
    [SqlFact]
    public async Task MissingSqlCannotPerformFilesystemChanges()
    {
        var r = await New();
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_missing", ConnectTimeout = 2 };
        var failed = r.Build(r.Disk, new FileTransactionStore(new Factory(builder.ConnectionString)));
        Assert.Equal(FileStatus.DatabaseUnavailable, (await failed.CreateAsync("", "no-db.txt", false)).Status);
        Assert.False(File.Exists(r.Physical("no-db.txt")));
    }
    [SqlFact]
    public async Task InterruptedUncommittedPublishBlocksCommandsUntilSafeRecovery()
    {
        var r = await New();
        using (var workspace = r.Disk.Open(r.User))
        {
            var journal = workspace.Stage(Guid.NewGuid(), ChangeKind.Create, "interrupted.txt");
            workspace.Publish(journal); // simulated process stopped before SQL commit
        }
        Assert.True(File.Exists(r.Physical("interrupted.txt")));
        Assert.Equal(FileStatus.RecoveryRequired, (await r.Files.CreateAsync("", "blocked.txt", false)).Status);
        Ok(await r.Files.RecoverAsync());
        Assert.False(File.Exists(r.Physical("interrupted.txt")));
        Ok(await r.Files.CreateAsync("", "after-recovery.txt", false));
    }
    [SqlFact]
    public async Task RecoveryDoesNotEraseAnUnexpectedExternalReplacement()
    {
        var r = await New();
        using (var workspace = r.Disk.Open(r.User))
        { var journal = workspace.Stage(Guid.NewGuid(), ChangeKind.Create, "interrupted.txt"); workspace.Publish(journal); }
        File.Delete(r.Physical("interrupted.txt")); await File.WriteAllTextAsync(r.Physical("interrupted.txt"), "external replacement");
        Assert.False((await r.Files.RecoverAsync()).Succeeded);
        Assert.Equal("external replacement", await File.ReadAllTextAsync(r.Physical("interrupted.txt")));
    }
    [SqlFact]
    public async Task SearchSortingAndSessionResetStayInsideCurrentDirectory()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "z.txt", false)); Ok(await r.Files.CreateAsync("", "a.txt", false));
        // WPF collection views are used on an STA in shell tests. Service returns only direct children.
        Ok(await r.Files.CreateAsync("", "folder", true)); Ok(await r.Files.CreateAsync("folder", "hidden.txt", false));
        Assert.DoesNotContain((await r.Files.ListAsync("")).Value!, x => x.Name == "hidden.txt");
        Assert.Single((await r.Files.ListAsync("folder")).Value!);
    }
    [SqlFact]
    public async Task JunctionHardLinksAndAncestorReplacementAreRejectedByFileAdapter()
    {
        var r = await New();
        var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "outside"); Directory.CreateDirectory(outside);
        var source = Path.Combine(outside, "sentinel.txt"); await File.WriteAllTextAsync(source, "test-only outside contents");
        SecurityIntegrationTests.CreateJunction(r.Physical("junction"), outside);
        Assert.Equal(EntryState.Unsafe, (await r.Entry("junction")).State);
        Assert.Equal(FileStatus.Denied, (await r.Files.SynchronizeAsync("junction")).Status);
        var externalLink = Path.Combine(Path.GetDirectoryName(r.Root)!, "source-junction");
        SecurityIntegrationTests.CreateJunction(externalLink, outside);
        Assert.Equal(FileStatus.Denied, (await r.Files.ImportAsync("", new(Path.Combine(externalLink, "sentinel.txt"), "import.txt"))).Status);
        Assert.True(SecurityIntegrationTests.CreateHardLink(r.Physical("hard-link.txt"), source, IntPtr.Zero));
        Assert.Equal(EntryState.Unsafe, (await r.Entry("hard-link.txt")).State);
        Assert.Equal(FileStatus.Denied, (await r.Files.SynchronizeAsync("hard-link.txt")).Status);
        Directory.CreateDirectory(r.Physical("pinned"));
        using (var space = r.Disk.Open(r.User))
        {
            var journal = space.Stage(Guid.NewGuid(), ChangeKind.Create, "pinned/inside.txt");
            Assert.ThrowsAny<IOException>(() => Directory.Move(r.Physical("pinned"), Path.Combine(outside, "moved")));
            space.Publish(journal); space.Settle(journal, false);
        }
        Assert.Equal("test-only outside contents", await File.ReadAllTextAsync(source));
    }
    [SqlFact]
    public async Task AmbiguousCommitIsVerifiedAndNeverRollsBackCommittedUserData()
    {
        var r = await New();
        var ambiguous = r.Build(r.Disk, new AmbiguousStore(new FileTransactionStore(r.Db), false));
        Assert.Equal(FileStatus.Conflict, (await ambiguous.CreateAsync("", "committed.txt", false)).Status);
        Assert.True(File.Exists(r.Physical("committed.txt")));
        var uncertain = r.Build(r.Disk, new AmbiguousStore(new FileTransactionStore(r.Db), true));
        Assert.Equal(FileStatus.RecoveryRequired, (await uncertain.CreateAsync("", "verify-later.txt", false)).Status);
        Assert.True(File.Exists(r.Physical("verify-later.txt")));
        Ok(await r.Files.RecoverAsync());
        Assert.True(File.Exists(r.Physical("verify-later.txt")));
        Assert.Equal(2, (await r.Files.ListAsync("")).Value!.Count);
    }
    private sealed class AmbiguousStore(IFileTransactionStore inner, bool disconnected) : IFileTransactionStore
    {
        public async Task<T> ExecuteAsync<T>(Guid owner, Func<IFileWork, Task<T>> action, CancellationToken ct)
        { await inner.ExecuteAsync(owner, action, ct); throw new FileStoreException(true); }
        public Task<bool> HasSuccessAsync(Guid owner, Guid id, CancellationToken ct) => disconnected ?
            throw new FileStoreException(true) : inner.HasSuccessAsync(owner, id, ct);
    }
    private sealed class SessionEndingStore(IFileTransactionStore inner, Action ending) : IFileTransactionStore
    {
        public async Task<T> ExecuteAsync<T>(Guid owner, Func<IFileWork, Task<T>> action, CancellationToken ct)
        { var value = await inner.ExecuteAsync(owner, action, ct); ending(); return value; }
        public Task<bool> HasSuccessAsync(Guid owner, Guid id, CancellationToken ct) => inner.HasSuccessAsync(owner, id, ct);
    }
    private sealed class FaultStorage(IPhysicalFileStorage inner, Action afterPublish) : IPhysicalFileStorage
    {
        public IFileWorkspace Open(Guid owner) => new FaultWorkspace(inner.Open(owner), afterPublish);
    }
    private sealed class FaultWorkspace(IFileWorkspace inner, Action afterPublish) : IFileWorkspace
    {
        public IReadOnlyList<PhysicalEntry> List(string path) => inner.List(path);
        public PhysicalEntry Inspect(string path) => inner.Inspect(path);
        public PhysicalText Read(string path) => inner.Read(path);
        public BinaryDocument OpenBinary(string path, long limit) => inner.OpenBinary(path, limit);
        public IReadOnlyList<FileJournal> Pending() => inner.Pending();
        public FileJournal Stage(Guid id, ChangeKind kind, string path, string? text = null, string? expected = null, ImportSelection? source = null) =>
            inner.Stage(id, kind, path, text, expected, source);
        public void Publish(FileJournal journal) { inner.Publish(journal); afterPublish(); }
        public FileJournal StageGenerated(Guid id, string path, bool directory, Action<IFilePreparation> prepare, CancellationToken ct) =>
            inner.StageGenerated(id, path, directory, prepare, ct);
        public void Settle(FileJournal journal, bool committed) => inner.Settle(journal, committed);
        public void Dispose() => inner.Dispose();
    }
}
