using System.IO;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Data.Files;
using SecureFileManager.Infrastructure.Windows.FileSystem;

namespace SecureFileManager.Tests;
public sealed partial class FileServiceTests
{
    [SqlFact]
    public async Task ExternalTrashPreservesTreeIdsHistoryAndRestoresWithoutOverwrite()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "tree");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(source, "binary.bin"), [0, 255, 1]);
        await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "preserve");
        var document = (await External(r).OpenAsync(Path.Combine(source, "note.txt"))).Value!;
        var ops = Operations(r);
        Ok(await ops.ExecuteAsync(new(ExternalChange.Delete, source, root, "tree")));
        Assert.False(Directory.Exists(source));
        var trash = Assert.Single(Directory.GetDirectories(root, ".sfm-trash-*"));
        Assert.True(Directory.Exists(Path.Combine(trash, "empty")));
        Assert.False((await External(r).ReadAsync(document.Id)).Succeeded);
        Directory.CreateDirectory(source);
        Assert.Equal(FileStatus.Conflict, (await ops.ExecuteAsync(new(ExternalChange.Restore, source, root, "tree"))).Status);
        Directory.Delete(source);
        Ok(await ops.ExecuteAsync(new(ExternalChange.Restore, source, root, "tree")));
        Assert.Equal("preserve", (await External(r).ReadAsync(document.Id)).Value!.Content);
        Assert.Equal(new byte[] { 0, 255, 1 }, await File.ReadAllBytesAsync(Path.Combine(source, "binary.bin")));
        Assert.True(Directory.Exists(Path.Combine(source, "empty"))); Assert.False(Directory.Exists(trash));
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(Path.Combine(source, "note.txt"), (await db.ExternalDocuments.SingleAsync(x => x.Id == document.Id)).Path);
        var history = await db.Operations.Where(x => x.UserId == r.User).ToListAsync();
        Assert.Contains(history, x => x.Details == "EXTERNAL_OP_Delete" && x.OperationType == OperationType.FILE_DELETED);
        Assert.Contains(history, x => x.Details == "EXTERNAL_OP_Restore");
        Assert.Contains(history, x => x.Details == "EXTERNAL_READ" && x.ExternalPathSnapshot == Path.Combine(source, "note.txt"));
        Assert.Empty(Directory.GetFiles(Path.Combine(r.Root, ".external-trash")));
    }
    [SqlFact]
    public async Task ExternalTrashAuditFailureRollsBackBothDirectionsAndKeepsReceipt()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "file.txt"); await File.WriteAllTextAsync(source, "original");
        await using var db = r.Db.CreateDbContext();
        const string trigger = "CREATE TRIGGER Stage1104Failure ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Details IN ('EXTERNAL_OP_Delete','EXTERNAL_OP_Restore','EXTERNAL_OP_CreateZip','EXTERNAL_OP_ExtractZip')) THROW 51104, 'Isolated test failure', 1; END;";
        await db.Database.ExecuteSqlRawAsync(trigger);
        try { Assert.False((await Operations(r).ExecuteAsync(new(ExternalChange.Delete, source, root, "file.txt"))).Succeeded); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage1104Failure;"); }
        Assert.Equal("original", await File.ReadAllTextAsync(source)); Assert.Empty(Directory.GetFiles(root, ".sfm-trash-*"));
        Ok(await Operations(r).ExecuteAsync(new(ExternalChange.Delete, source, root, "file.txt")));
        await db.Database.ExecuteSqlRawAsync(trigger);
        try { Assert.False((await Operations(r).ExecuteAsync(new(ExternalChange.Restore, source, root, "file.txt"))).Succeeded); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage1104Failure;"); }
        Assert.False(File.Exists(source)); Assert.Single(Directory.GetFiles(root, ".sfm-trash-*"));
        Ok(await Operations(r).ExecuteAsync(new(ExternalChange.Restore, source, root, "file.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(source));
    }
    [SqlFact]
    public async Task ExternalTrashInterruptedDeleteAndRestoreRecoverAndKeepChangedObjects()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "file.txt"); await File.WriteAllTextAsync(source, "original");
        var disk = new WindowsExternalOperationStorage(r.Paths); ExternalOperationPlan plan;
        using (var space = disk.Open()) { plan = space.Prepare(new(ExternalChange.Delete, source, root, "file.txt"), default); space.Publish(plan, default); }
        Ok(await Operations(r).RecoverAsync()); Assert.Equal("original", await File.ReadAllTextAsync(source));
        Ok(await Operations(r).ExecuteAsync(new(ExternalChange.Delete, source, root, "file.txt")));
        using (var space = disk.Open()) { plan = space.Prepare(new(ExternalChange.Restore, source, root, "file.txt"), default); space.Publish(plan, default); }
        Ok(await Operations(r).RecoverAsync()); Assert.False(File.Exists(source));
        Ok(await Operations(r).ExecuteAsync(new(ExternalChange.Restore, source, root, "file.txt")));
        using (var space = disk.Open()) { plan = space.Prepare(new(ExternalChange.Delete, source, root, "file.txt"), default); space.Publish(plan, default); }
        await File.WriteAllTextAsync(plan.Target, "external change");
        Assert.Equal(FileStatus.RecoveryRequired, (await Operations(r).RecoverAsync()).Status);
        Assert.Equal("external change", await File.ReadAllTextAsync(plan.Target));
    }
    [SqlFact]
    public async Task ExternalTrashCommittedRecoveryRetainsReceiptAndAnotherAccountCanRestore()
    {
        var r = await New(); var other = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "file.txt"); await File.WriteAllTextAsync(source, "shared");
        Assert.Equal(FileStatus.RecoveryRequired, (await Operations(r, new BlockOperationCleanup(new WindowsExternalOperationStorage(r.Paths))).ExecuteAsync(new(ExternalChange.Delete, source, root, "file.txt"))).Status);
        r.Session.Set(other.User); Ok(await Operations(r).RecoverAsync()); Assert.False(File.Exists(source));
        Ok(await Operations(r).ExecuteAsync(new(ExternalChange.Restore, source, root, "file.txt")));
        Assert.Equal("shared", await File.ReadAllTextAsync(source));
    }
    [SqlFact]
    public async Task ExternalZipRoundTripPreservesBinaryEmptyFoldersAndMetadataAudit()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "tree"); Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), [0, 255, 2, 3]);
        var ops = Operations(r); Ok(await ops.ExecuteAsync(new(ExternalChange.CreateZip, source, root, "tree.zip")));
        var zip = Path.Combine(root, "tree.zip"); var preview = await ops.PreviewAsync(zip); Ok(preview);
        Assert.True(preview.Value!.CanExtract); Assert.Contains(preview.Value.Entries, x => x.Path == "tree/empty/");
        Ok(await ops.ExecuteAsync(new(ExternalChange.ExtractZip, zip, root, "output", preview.Value.Version)));
        Assert.Equal(new byte[] { 0, 255, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "output", "tree", "data.bin")));
        Assert.True(Directory.Exists(Path.Combine(root, "output", "tree", "empty")));
        await using var db = r.Db.CreateDbContext();
        Assert.True(await db.ExternalDocuments.AnyAsync(x => x.Path == Path.Combine(root, "output", "tree", "data.bin")));
        Assert.True(await db.Operations.AnyAsync(x => x.UserId == r.User && x.OperationType == OperationType.ZIP_CREATED && x.Result == OperationResult.Success));
        Assert.True(await db.Operations.AnyAsync(x => x.UserId == r.User && x.OperationType == OperationType.ZIP_EXTRACTED && x.Result == OperationResult.Success));
        Assert.Empty(Directory.GetFileSystemEntries(root, ".sfm-op-*"));
    }
    [SqlFact]
    public async Task ExternalZipRejectsSlipLinksBombDuplicatesCrcCorruptionAndPreservesSentinel()
    {
        var r = await New(); var root = OperationRoot(r); var zip = Path.Combine(root, "unsafe.zip"); var ops = Operations(r);
        var link = ZipEngineTests.Zip(("link", "x")); var central = ZipEngineTests.Header(link, 0x02014b50); ZipEngineTests.Number(link, central + 38, 0xA1FF0000);
        var crc = ZipEngineTests.Zip(("bad.txt", "content")); var local = ZipEngineTests.Header(crc, 0x04034b50); central = ZipEngineTests.Header(crc, 0x02014b50);
        ZipEngineTests.Number(crc, local + 14, 123); ZipEngineTests.Number(crc, central + 16, 123);
        byte[] bomb;
        using (var data = new MemoryStream())
        {
            using (var archive = new ZipArchive(data, ZipArchiveMode.Create, true))
            { using var writer = new StreamWriter(archive.CreateEntry("bomb.txt", CompressionLevel.SmallestSize).Open()); writer.Write(new string('x', 100000)); }
            bomb = data.ToArray();
        }
        var sentinel = Path.Combine(root, "outside.txt"); await File.WriteAllTextAsync(sentinel, "sentinel");
        foreach (var bytes in new[] { ZipEngineTests.Zip(("../outside.txt", "attack")), ZipEngineTests.Zip(("same", "a"), ("SAME", "b")), link, bomb, crc, new byte[] { 1, 2, 3 } })
        {
            await File.WriteAllBytesAsync(zip, bytes);
            var result = await ops.ExecuteAsync(new(ExternalChange.ExtractZip, zip, root, "output"));
            Assert.False(result.Succeeded); Assert.NotEqual(FileStatus.DatabaseUnavailable, result.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "output"))); Assert.Empty(Directory.GetFileSystemEntries(root, ".sfm-op-*"));
            Assert.Equal("sentinel", await File.ReadAllTextAsync(sentinel));
        }
    }
    [SqlFact]
    public async Task ExternalZipPreviewVersionAndDestinationConflictsPreserveExistingData()
    {
        var r = await New(); var root = OperationRoot(r); var zip = Path.Combine(root, "data.zip"); var ops = Operations(r);
        await File.WriteAllBytesAsync(zip, ZipEngineTests.Zip(("file.txt", "first")));
        var preview = (await ops.PreviewAsync(zip)).Value!;
        await File.WriteAllBytesAsync(zip, ZipEngineTests.Zip(("file.txt", "second")));
        Assert.Equal(FileStatus.Conflict, (await ops.ExecuteAsync(new(ExternalChange.ExtractZip, zip, root, "output", preview.Version))).Status);
        Directory.CreateDirectory(Path.Combine(root, "output")); await File.WriteAllTextAsync(Path.Combine(root, "output", "sentinel"), "existing");
        Assert.Equal(FileStatus.Conflict, (await ops.ExecuteAsync(new(ExternalChange.ExtractZip, zip, root, "output"))).Status);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(root, "output", "sentinel")));
        Assert.Equal(FileStatus.Invalid, (await ops.ExecuteAsync(new(ExternalChange.CreateZip, root, root, "inside.zip"))).Status);
    }
    [SqlFact]
    public async Task ExternalZipAuditFailureAndUncommittedRecoveryRemoveOnlyGeneratedData()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(source, "original");
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage1104ZipFailure ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Details IN ('EXTERNAL_OP_CreateZip','EXTERNAL_OP_ExtractZip')) THROW 51104, 'Isolated test failure', 1; END;");
        var zip = Path.Combine(root, "safe.zip"); await File.WriteAllBytesAsync(zip, ZipEngineTests.Zip(("file.txt", "payload")));
        try
        {
            Assert.False((await Operations(r).ExecuteAsync(new(ExternalChange.CreateZip, source, root, "failed.zip"))).Succeeded);
            Assert.False((await Operations(r).ExecuteAsync(new(ExternalChange.ExtractZip, zip, root, "failed"))).Succeeded);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage1104ZipFailure;"); }
        Assert.False(File.Exists(Path.Combine(root, "failed.zip"))); Assert.False(Directory.Exists(Path.Combine(root, "failed")));
        foreach (var request in new[] { new ExternalChangeRequest(ExternalChange.CreateZip, source, root, "pending.zip"), new(ExternalChange.ExtractZip, zip, root, "pending") })
        {
            using (var space = new WindowsExternalOperationStorage(r.Paths).Open()) { var plan = space.Prepare(request, default); space.Publish(plan, default); }
            Ok(await Operations(r).RecoverAsync());
            Assert.False(File.Exists(Path.Combine(root, request.Name))); Assert.False(Directory.Exists(Path.Combine(root, request.Name)));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(source)); Assert.True(File.Exists(zip));
    }
    [SqlFact]
    public async Task ExternalTrashAndZipFailClosedWithoutSqlSessionAndForPrivateOrLinkedPaths()
    {
        var r = await New(); var root = OperationRoot(r); var source = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(source, "original");
        var missing = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "Missing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 1 };
        var noSql = Operations(r, store: new ExternalDocumentStore(new Factory(missing.ConnectionString)));
        foreach (var kind in new[] { ExternalChange.Delete, ExternalChange.Restore, ExternalChange.CreateZip, ExternalChange.ExtractZip })
            Assert.Equal(FileStatus.DatabaseUnavailable, (await noSql.ExecuteAsync(new(kind, source, root, "target.zip"))).Status);
        Assert.Equal(FileStatus.DatabaseUnavailable, (await noSql.PreviewAsync(source)).Status);
        r.Session.Set(null); Assert.Equal(FileStatus.Denied, (await Operations(r).ExecuteAsync(new(ExternalChange.Delete, source, root, "source.txt"))).Status); r.Session.Set(r.User);
        Assert.Equal(FileStatus.Denied, (await Operations(r).ExecuteAsync(new(ExternalChange.Delete, r.Physical(""), root, "no"))).Status);
        SecurityIntegrationTests.CreateJunction(Path.Combine(root, "link"), r.Physical(""));
        Assert.Equal(FileStatus.Denied, (await Operations(r).ExecuteAsync(new(ExternalChange.Delete, Path.Combine(root, "link"), root, "no"))).Status);
        Assert.Equal(FileStatus.Denied, (await Operations(r).ExecuteAsync(new(ExternalChange.CreateZip, source, r.Physical(""), "no.zip"))).Status);
        Assert.Equal("original", await File.ReadAllTextAsync(source));
    }
}
