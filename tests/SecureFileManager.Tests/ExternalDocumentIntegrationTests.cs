using SecureFileManager.Cli;
using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Services;
using SecureFileManager.Data.Files;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Infrastructure.Windows.FileSystem;


namespace SecureFileManager.Tests;
public sealed partial class FileServiceTests
{
    private ExternalDocumentService External(Rig r, IExternalDocumentStorage? disk = null, IExternalDocumentStore? store = null) =>
        new(r.Session, store ?? new ExternalDocumentStore(r.Db), disk ?? new WindowsExternalDocumentStorage(r.Paths),
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock()), new SystemClock());
    private static async Task<string> ExternalFile(Rig r, string name, string text)
    {
        var parent = Path.Combine(Path.GetDirectoryName(r.Root)!, "external-documents"); Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, name); await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)); return path;
    }
    [SqlFact]
    public async Task ExternalSchemaReadinessDetectsPendingMigrationWithoutUpdatingDatabase()
    {
        await using var transaction = await database.BeginAsync();
        await using var db = transaction.CreateDbContext();
        var check = new SecureFileManager.Data.Identity.DatabaseConnectionCheck(transaction);
        Assert.True(await check.CanConnectAsync());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId]='20261107101211_AddExternalDocuments';");
        Assert.False(await check.CanConnectAsync());
        // Dispose rolls back this test-only history change; physical schema is untouched.
    }
    [SqlFact]
    public async Task ExternalDocumentsRoundTripAndShareIdentityWithSeparateActorAudit()
    {
        var r = await New(); var other = await New(); var service = External(r);
        foreach (var (name, before, after) in new[] { ("one.txt", "до", "после"), ("two.json", "{}", "{\"demo\":true}"), ("three.xml", "<d/>", "<d>текст</d>") })
        {
            var path = await ExternalFile(r, name, before); var opened = await service.OpenAsync(path); Ok(opened);
            Ok(await service.SaveAsync(opened.Value!.Id, after, opened.Value.Version));
            Assert.Equal(after, await File.ReadAllTextAsync(path));
            await using (var verify = r.Db.CreateDbContext())
            {
                var metadata = await verify.ExternalDocuments.SingleAsync(x => x.Id == opened.Value.Id);
                Assert.Equal(new FileInfo(path).Length, metadata.SizeBytes);
                Assert.Equal(File.GetLastWriteTimeUtc(path), metadata.ModifiedAtUtc);
            }
            var firstId = opened.Value.Id; r.Session.Set(other.User);
            var shared = await service.OpenAsync(path); Ok(shared); Assert.Equal(firstId, shared.Value!.Id); Assert.Equal(after, shared.Value.Content);
            r.Session.Set(r.User);
        }
        await using var db = r.Db.CreateDbContext();
        Assert.Equal(3, await db.ExternalDocuments.CountAsync()); Assert.False(await db.Files.AnyAsync(x => x.OwnerId == r.User));
        var saved = await db.Operations.Where(x => x.UserId == r.User && x.Details == "EXTERNAL_SAVED").ToListAsync();
        Assert.Equal(3, saved.Count); Assert.All(saved, x => { Assert.NotNull(x.ExternalDocumentId); Assert.NotNull(x.ExternalPathSnapshot); Assert.Null(x.FileId); Assert.Equal(OperationResult.Success, x.Result); });
        var journal = await Journal(r).GetAsync(new(Search: "external-documents"));
        Assert.True(journal.IsSuccess); Assert.Contains(journal.Value!.Items, x => AuditListItem.From(x).Target.Contains("external-documents"));
        Assert.All(journal.Value.Items, x => Assert.Equal(r.User, x.UserId));
    }
    [SqlFact]
    public async Task ExternalStaleVersionAndWindowsWriterConflictPreserveExistingData()
    {
        var r = await New(); var service = External(r); var path = await ExternalFile(r, "conflict.txt", "before");
        var first = (await service.OpenAsync(path)).Value!; var other = await New(); r.Session.Set(other.User);
        var second = (await service.OpenAsync(path)).Value!; Ok(await service.SaveAsync(second.Id, "second user", second.Version));
        r.Session.Set(r.User); Assert.Equal(FileStatus.Conflict, (await service.SaveAsync(first.Id, "stale", first.Version)).Status);
        var latest = (await service.ReadAsync(first.Id)).Value!;
        await File.WriteAllTextAsync(path, "external program");
        Assert.Equal(FileStatus.Conflict, (await service.SaveAsync(latest.Id, "stale again", latest.Version)).Status);
        Assert.Equal("external program", await File.ReadAllTextAsync(path));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False((await service.ReadAsync(first.Id)).Succeeded);
    }
    [SqlFact]
    public async Task ExternalInvalidDocumentsXxeEncodingLimitsAndBomAreEnforced()
    {
        var r = await New(); var service = External(r);
        var json = await ExternalFile(r, "valid.json", "{}"); var opened = (await service.OpenAsync(json)).Value!;
        Assert.Equal(FileStatus.InvalidDocument, (await service.SaveAsync(opened.Id, "{", opened.Version)).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(json));
        var dtd = await ExternalFile(r, "dtd.xml", "<!DOCTYPE d [<!ENTITY t SYSTEM 'file:///never-read'>]><d>&t;</d>");
        Assert.Equal(FileStatus.InvalidDocument, (await service.OpenAsync(dtd)).Status);
        var bad = await ExternalFile(r, "bad.txt", "x"); await File.WriteAllBytesAsync(bad, [0xFF,0xFE]); Assert.False((await service.OpenAsync(bad)).Succeeded);
        var big = await ExternalFile(r, "big.txt", "x"); using (var stream = File.OpenWrite(big)) stream.SetLength(FileRules.TextLimit + 1);
        Assert.Equal(FileStatus.LimitExceeded, (await service.OpenAsync(big)).Status);
        var bom = await ExternalFile(r, "bom.txt", ""); await File.WriteAllTextAsync(bom, "first\r\nline", new UTF8Encoding(true));
        var document = (await service.OpenAsync(bom)).Value!; Ok(await service.SaveAsync(document.Id, "new\r\nline", document.Version));
        Assert.True((await File.ReadAllBytesAsync(bom)).AsSpan().StartsWith(new byte[] {239,187,191}));
    }
    [SqlFact]
    public async Task ExternalAuditInsertFailureRestoresBytesAndDoesNotRegisterSuccess()
    {
        var r = await New(); var service = External(r); var path = await ExternalFile(r, "audit.txt", "original");
        var document = (await service.OpenAsync(path)).Value!;
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER ExternalTestFailure ON Operations AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Details='EXTERNAL_SAVED') THROW 51011, 'Test-only failure', 1; END;");
        try { Assert.False((await service.SaveAsync(document.Id, "new data", document.Version)).Succeeded); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER ExternalTestFailure;"); }
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        var retained = await db.ExternalDocuments.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Equal(retained.ModifiedAtUtc, File.GetLastWriteTimeUtc(path));
        Assert.False(await db.Operations.AnyAsync(x => x.ExternalDocumentId == document.Id && x.Details == "EXTERNAL_SAVED"));
        var retry = (await service.ReadAsync(document.Id)).Value!; Ok(await service.SaveAsync(retry.Id, "retry", retry.Version));
    }
    [SqlFact]
    public async Task ExternalInterruptedSaveRestoresOnlyUncommittedOriginalAndProtectsUnexpectedBytes()
    {
        var r = await New(); var service = External(r); var path = await ExternalFile(r, "recovery.txt", "original");
        Ok(await service.OpenAsync(path)); var disk = new WindowsExternalDocumentStorage(r.Paths);
        using (var file = disk.Open(path, true)) { var old = file.Read(); file.Prepare(Guid.NewGuid(), "uncommitted", old.Version); file.Publish(); }
        Assert.Equal(FileStatus.RecoveryRequired, (await service.OpenAsync(path)).Status);
        Ok(await service.RecoverAsync(path)); Assert.Equal("original", await File.ReadAllTextAsync(path));
        using (var file = disk.Open(path, true)) { var old = file.Read(); file.Prepare(Guid.NewGuid(), "uncommitted", old.Version); file.Publish(); }
        await File.WriteAllTextAsync(path, "unexpected external bytes");
        Assert.False((await service.RecoverAsync(path)).Succeeded);
        Assert.Equal("unexpected external bytes", await File.ReadAllTextAsync(path));
    }
    [SqlFact]
    public async Task ExternalCommittedRecoveryByAnotherUserNeverRollsBackCommittedData()
    {
        var r = await New(); var other = await New(); var path = await ExternalFile(r, "committed.txt", "original");
        var service = External(r, new CleanupBlockedStorage(new WindowsExternalDocumentStorage(r.Paths)));
        var document = (await service.OpenAsync(path)).Value!;
        Assert.Equal(FileStatus.RecoveryRequired, (await service.SaveAsync(document.Id, "committed data", document.Version)).Status);
        r.Session.Set(other.User); Ok(await External(r).RecoverAsync(path));
        Assert.Equal("committed data", await File.ReadAllTextAsync(path));
    }
    private sealed class CleanupBlockedStorage(IExternalDocumentStorage inner) : IExternalDocumentStorage
    {
        public IExternalDocumentFile Open(string path, bool writable, bool recovery = false) => new CleanupBlockedFile(inner.Open(path, writable, recovery));
        private sealed class CleanupBlockedFile(IExternalDocumentFile file) : IExternalDocumentFile
        {
            public string Path => file.Path; public Guid? Pending => file.Pending; public ExternalText Read() => file.Read();
            public void Prepare(Guid correlation, string text, string expectedVersion) => file.Prepare(correlation, text, expectedVersion);
            public void Publish() => file.Publish(); public void Settle(bool committed) => throw new FileCommandException(FileStatus.RecoveryRequired);
            public void Dispose() => file.Dispose();
        }
    }
    [SqlFact]
    public async Task ExternalPrivateStorageTraversalJunctionAndReadOnlyAreRejected()
    {
        var r = await New(); var service = External(r);
        await File.WriteAllTextAsync(r.Physical("private.txt"), "private");
        Assert.Equal(FileStatus.Denied, (await service.OpenAsync(r.Physical("private.txt"))).Status);
        Assert.False((await service.OpenAsync(@"C:\..\Windows\note.txt")).Succeeded);
        var path = await ExternalFile(r, "readonly.txt", "preserve"); var document = (await service.OpenAsync(path)).Value!;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Assert.False((await service.SaveAsync(document.Id, "replace", document.Version)).Succeeded); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.Equal("preserve", await File.ReadAllTextAsync(path));
        var link = Path.Combine(Path.GetDirectoryName(path)!, "link"); SecurityIntegrationTests.CreateJunction(link, r.Physical(""));
        Assert.Equal(FileStatus.Denied, (await service.OpenAsync(Path.Combine(link, "private.txt"))).Status);
        r.Session.Set(null); Assert.Equal(FileStatus.Denied, (await service.OpenAsync(path)).Status);
    }
}
