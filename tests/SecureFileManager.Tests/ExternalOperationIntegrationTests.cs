using SecureFileManager.Cli;
using System.IO;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Services;
using SecureFileManager.Data.Files;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Infrastructure.Windows.FileSystem;


namespace SecureFileManager.Tests;
public sealed partial class FileServiceTests
{
    private IExternalFileOperationService Operations(Rig r, IExternalOperationStorage? disk = null, IExternalDocumentStore? store = null) =>
        new ExternalFileOperationService(r.Session, store ?? new ExternalDocumentStore(r.Db), disk ?? new WindowsExternalOperationStorage(r.Paths),
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock()), new SystemClock());
    private static string OperationRoot(Rig r)
    { var root = Path.Combine(Path.GetDirectoryName(r.Root)!, "operations"); Directory.CreateDirectory(root); return root; }
    [SqlFact]
    public async Task ExternalOperationsCreateFilesFoldersAndAuditWithoutPersonalMetadata()
    {
        var r = await New(); var root = OperationRoot(r); var ops = Operations(r);
        Ok(await ops.ExecuteAsync(new(ExternalChange.CreateDirectory, null, root, "folder")));
        foreach (var name in new[] { "demo.txt", "demo.json", "demo.xml", "empty.bin", "name' OR 1=1--.txt" })
            Ok(await ops.ExecuteAsync(new(ExternalChange.CreateFile, null, Path.Combine(root, "folder"), name)));
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(root, "folder", "demo.json")));
        Assert.Equal("<document />", await File.ReadAllTextAsync(Path.Combine(root, "folder", "demo.xml")));
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(5, await db.ExternalDocuments.CountAsync(x => x.Path.StartsWith(root)));
        Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User));
        var journal = (await Journal(r).GetAsync(new(Search: root))).Value!;
        Assert.Contains(journal.Items, x => x.Details == "EXTERNAL_OP_CreateDirectory" && AuditListItem.From(x).Target == Path.Combine(root, "folder"));
        Assert.Equal(5, journal.Items.Count(x => x.Details == "EXTERNAL_OP_CreateFile"));
    }
    [SqlFact]
    public async Task ExternalOperationsCopyTreeAndMovePreserveBytesIdsAndDeletedHistorySnapshots()
    {
        var r = await New(); var root = OperationRoot(r); var ops = Operations(r);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(source, "binary.bin"), [0,1,2,255]);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "note.txt"), "preserved");
        var document = (await External(r).OpenAsync(Path.Combine(source, "nested", "note.txt"))).Value!;
        Ok(await ops.ExecuteAsync(new(ExternalChange.Copy, source, root, "copy")));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, "binary.bin")), await File.ReadAllBytesAsync(Path.Combine(root, "copy", "binary.bin")));
        Assert.True(Directory.Exists(Path.Combine(root, "copy", "empty")));
        Ok(await ops.ExecuteAsync(new(ExternalChange.Move, source, root, "renamed")));
        Assert.False(Directory.Exists(source)); Assert.True(Directory.Exists(Path.Combine(root, "renamed", "empty")));
        var read = await External(r).ReadAsync(document.Id); Ok(read); Assert.Equal("preserved", read.Value!.Content);
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(Path.Combine(root, "renamed", "nested", "note.txt"), (await db.ExternalDocuments.SingleAsync(x => x.Id == document.Id)).Path);
        Assert.NotEqual(document.Id, (await db.ExternalDocuments.SingleAsync(x => x.Path == Path.Combine(root, "copy", "nested", "note.txt"))).Id);
        Assert.Contains(await db.Operations.Where(x => x.UserId == r.User).ToListAsync(), x => x.Details == "EXTERNAL_READ" && x.ExternalPathSnapshot == Path.Combine(source, "nested", "note.txt"));
        Assert.Equal(FileStatus.Conflict, (await External(r).SaveAsync(document.Id, "old buffer", document.Version)).Status);
    }
    [SqlFact]
    public async Task ExternalOperationsConflictsTraversalSelfCopyLinksAndPrivateStorageDoNotChangeData()
    {
        var r = await New(); var root = OperationRoot(r); var ops = Operations(r);
        await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "sentinel");
        Assert.Equal(FileStatus.Conflict, (await ops.ExecuteAsync(new(ExternalChange.CreateFile, null, root, "EXISTING.txt"))).Status);
        foreach (var name in new[] { "../bad", "NUL.txt", "absolute:C", ".sfm-internal" })
            Assert.Equal(FileStatus.Invalid, (await ops.ExecuteAsync(new(ExternalChange.CreateFile, null, root, name))).Status);
        Assert.Equal(FileStatus.Denied, (await ops.ExecuteAsync(new(ExternalChange.CreateFile, null, r.Physical(""), "bad.txt"))).Status);
        var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
        Assert.Equal(FileStatus.Invalid, (await ops.ExecuteAsync(new(ExternalChange.Copy, root, child, "inside"))).Status);
        SecurityIntegrationTests.CreateJunction(Path.Combine(root, "junction"), r.Physical(""));
        Assert.Equal(FileStatus.Denied, (await ops.ExecuteAsync(new(ExternalChange.Copy, Path.Combine(root, "junction"), root, "bad-copy"))).Status);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(Path.Combine(root, "existing.txt")));
        Assert.False(File.Exists(r.Physical("bad.txt")));
    }
    [SqlFact]
    public async Task ExternalOperationsAuditFailureRollsBackCreateCopyAndMove()
    {
        var r = await New(); var root = OperationRoot(r); var path = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(path, "original");
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage1103AuditFailure ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Details LIKE 'EXTERNAL_OP_%') THROW 51103, 'Test-only audit failure', 1; END;");
        try
        {
            foreach (var kind in new[] { ExternalChange.CreateFile, ExternalChange.Copy, ExternalChange.Move })
            {
                var result = await Operations(r).ExecuteAsync(new(kind, kind == ExternalChange.CreateFile ? null : path, root, "target.txt"));
                Assert.False(result.Succeeded); Assert.False(File.Exists(Path.Combine(root, "target.txt")));
                Assert.Equal("original", await File.ReadAllTextAsync(path));
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage1103AuditFailure;"); }
        Assert.False(await db.ExternalDocuments.AnyAsync(x => x.Path == Path.Combine(root, "target.txt")));
        using var workspace = new WindowsExternalOperationStorage(r.Paths).Open(); Assert.Empty(workspace.Pending());
    }
    [SqlFact]
    public async Task ExternalOperationsInterruptedCopyAndMoveRecoverWithoutDeletingUnexpectedData()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(source, "original");
        var disk = new WindowsExternalOperationStorage(r.Paths);
        foreach (var kind in new[] { ExternalChange.Copy, ExternalChange.Move })
        {
            using (var space = disk.Open()) { var plan = space.Prepare(new(kind, source, root, "pending.txt"), default); space.Publish(plan, default); }
            Assert.Equal(FileStatus.RecoveryRequired, (await External(r).OpenAsync(Path.Combine(root, "pending.txt"))).Status);
            Assert.Equal(FileStatus.RecoveryRequired, (await Operations(r).ExecuteAsync(new(ExternalChange.CreateFile, null, root, "blocked.txt"))).Status);
            Ok(await Operations(r).RecoverAsync()); Assert.True(File.Exists(source)); Assert.False(File.Exists(Path.Combine(root, "pending.txt")));
        }
        using (var space = disk.Open()) { var plan = space.Prepare(new(ExternalChange.Copy, source, root, "pending.txt"), default); space.Publish(plan, default); }
        await File.WriteAllTextAsync(Path.Combine(root, "pending.txt"), "unexpected");
        Assert.False((await Operations(r).RecoverAsync()).Succeeded);
        Assert.Equal("unexpected", await File.ReadAllTextAsync(Path.Combine(root, "pending.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(source));
    }
    [SqlFact]
    public async Task ExternalOperationsCommittedCleanupFailureRecoversUnderAnotherUser()
    {
        var r = await New(); var other = await New(); var root = OperationRoot(r);
        var disk = new BlockOperationCleanup(new WindowsExternalOperationStorage(r.Paths));
        Assert.Equal(FileStatus.RecoveryRequired, (await Operations(r, disk).ExecuteAsync(new(ExternalChange.CreateFile, null, root, "committed.txt"))).Status);
        r.Session.Set(other.User); Ok(await Operations(r).RecoverAsync()); Assert.True(File.Exists(Path.Combine(root, "committed.txt")));
    }
    private sealed class BlockOperationCleanup(IExternalOperationStorage inner) : IExternalOperationStorage
    {
        public IExternalOperationWorkspace Open() => new Space(inner.Open());
        private sealed class Space(IExternalOperationWorkspace inner) : IExternalOperationWorkspace
        {
            public IReadOnlyList<ExternalOperationPlan> Pending() => inner.Pending();
            public ExternalOperationPlan Prepare(ExternalChangeRequest request, CancellationToken ct) => inner.Prepare(request, ct);
            public IReadOnlyList<ExternalOperationEntry> Publish(ExternalOperationPlan plan, CancellationToken ct) => inner.Publish(plan, ct);
            public void Settle(ExternalOperationPlan plan, bool committed) => throw new FileCommandException(FileStatus.RecoveryRequired);
            public void Dispose() => inner.Dispose();
        }
    }
    [SqlFact]
    public async Task ExternalOperationsFolderRollbackAndLimitsPreserveSourceTree()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested")); await File.WriteAllTextAsync(Path.Combine(source, "nested", "data.txt"), "preserve");
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage1103TreeFailure ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Details IN ('EXTERNAL_OP_Move','EXTERNAL_OP_Copy')) THROW 51104, 'Test-only failure', 1; END;");
        try
        {
            foreach (var kind in new[] { ExternalChange.Copy, ExternalChange.Move })
            {
                Assert.False((await Operations(r).ExecuteAsync(new(kind, source, root, "target"))).Succeeded);
                Assert.False(Directory.Exists(Path.Combine(root, "target")));
                Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(source, "nested", "data.txt")));
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage1103TreeFailure;"); }
        for (var i = 0; i < ExternalOperationRules.Nodes; i++) Directory.CreateDirectory(Path.Combine(source, "empty" + i));
        Assert.Equal(FileStatus.LimitExceeded, (await Operations(r).ExecuteAsync(new(ExternalChange.Copy, source, root, "too-many"))).Status);
        Assert.False(Directory.Exists(Path.Combine(root, "too-many")));
    }
    [SqlFact]
    public async Task ExternalOperationsConcurrentCreateHasSingleWinnerAndCrossVolumeMoveIsExplicitlyRejected()
    {
        var r = await New(); var root = OperationRoot(r); var request = new ExternalChangeRequest(ExternalChange.CreateFile, null, root, "same.txt");
        var results = await Task.WhenAll(Operations(r).ExecuteAsync(request), Operations(r).ExecuteAsync(request));
        Assert.Single(results, x => x.Succeeded);
        Assert.True(File.Exists(Path.Combine(root, "same.txt")));
        var otherDrive = DriveInfo.GetDrives().FirstOrDefault(x => x.DriveType == DriveType.Fixed && x.IsReady &&
            !x.Name.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
        if (otherDrive is not null)
        {
            Assert.Equal(FileStatus.Unsupported, (await Operations(r).ExecuteAsync(new(ExternalChange.Move, Path.Combine(root, "same.txt"), otherDrive.Name, "sfm-check-" + Guid.NewGuid().ToString("N") + ".txt"))).Status);
            Assert.True(File.Exists(Path.Combine(root, "same.txt")));
        }
    }
    [SqlFact]
    public async Task ExternalOperationsMissingSqlInactiveSessionAndPendingEditorBlockChanges()
    {
        var r = await New(); var root = OperationRoot(r); var request = new ExternalChangeRequest(ExternalChange.CreateFile, null, root, "no-db.txt");
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "Missing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 1 };
        Assert.Equal(FileStatus.DatabaseUnavailable, (await Operations(r, store: new ExternalDocumentStore(new Factory(connection.ConnectionString))).ExecuteAsync(request)).Status);
        Assert.False(File.Exists(Path.Combine(root, "no-db.txt")));
        r.Session.Set(null); Assert.Equal(FileStatus.Denied, (await Operations(r).ExecuteAsync(request)).Status); r.Session.Set(r.User);
        var source = await ExternalFile(r, "editor-pending.txt", "before");
        using (var file = new WindowsExternalDocumentStorage(r.Paths).Open(source, true)) { file.Prepare(Guid.NewGuid(), "after", file.Read().Version); file.Publish(); }
        Assert.Equal(FileStatus.RecoveryRequired, (await Operations(r).ExecuteAsync(new(ExternalChange.Move, source, root, "moved.txt"))).Status);
        Assert.True(File.Exists(source)); Assert.False(File.Exists(Path.Combine(root, "moved.txt")));
    }
}
