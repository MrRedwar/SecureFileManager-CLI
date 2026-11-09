using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Data.Files;


namespace SecureFileManager.Tests;

public sealed partial class FileServiceTests
{
    [SqlFact]
    public async Task StructuredDocumentsRoundTripWithMetadataAndExistingAudit()
    {
        var r = await New();
        foreach (var pair in new[] { ("round.JSON", "{\r\n  \"имя\": \"Ра\", \"n\": 1.2300e+20\r\n}"), ("round.XML", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<root x=\"1\">Текст &amp; данные</root>") })
        {
            Ok(await r.Files.CreateAsync("", pair.Item1, false));
            var file = await r.Entry(pair.Item1); var original = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
            var check = await r.Files.ValidateAsync(original.Id, pair.Item2); Ok(check); Assert.True(check.Value!.IsValid);
            Ok(await r.Files.SaveAsync(original.Id, pair.Item2, original.Version));
            var reopened = await r.Files.ReadAsync(original.Id); Ok(reopened); Assert.Equal(pair.Item2, reopened.Value!.Content);
            Assert.Equal(pair.Item2, await File.ReadAllTextAsync(r.Physical(pair.Item1)));
            await using var db = r.Db.CreateDbContext();
            Assert.Equal(Encoding.UTF8.GetByteCount(pair.Item2), (await db.Files.SingleAsync(x => x.Id == original.Id)).SizeBytes);
            var events = await db.Operations.Where(x => x.UserId == r.User && x.FileId == original.Id).ToListAsync();
            Assert.Contains(events, x => x.OperationType == OperationType.FILE_UPDATED && x.Result == OperationResult.Success);
            Assert.Contains(events, x => x.OperationType == (pair.Item1.EndsWith("JSON") ? OperationType.JSON_VALIDATED : OperationType.XML_VALIDATED) && x.Result == OperationResult.Success);
        }
    }
    [SqlFact]
    public async Task InvalidStructuredSavesKeepOriginalBytesMetadataAndAuditSafe()
    {
        var r = await New();
        foreach (var name in new[] { "invalid.json", "invalid.xml" })
        {
            Ok(await r.Files.CreateAsync("", name, false)); var file = await r.Entry(name);
            var original = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
            var bytes = await File.ReadAllBytesAsync(r.Physical(name));
            foreach (var invalid in name.EndsWith("json") ? new[] { "", "{\"SECRET_FRAGMENT\":}", new string('[', 65) + "0" + new string(']', 65) } : new[] { "", "<SECRET_FRAGMENT>", "<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///forbidden'>]><r>&x;</r>" })
            {
                var validation = await r.Files.ValidateAsync(original.Id, invalid); Ok(validation); Assert.False(validation.Value!.IsValid);
                Assert.Equal(FileStatus.InvalidDocument, (await r.Files.SaveAsync(original.Id, invalid, original.Version)).Status);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(r.Physical(name)));
            }
            await using var db = r.Db.CreateDbContext();
            Assert.Equal(bytes.Length, (await db.Files.SingleAsync(x => x.Id == original.Id)).SizeBytes);
            var events = await db.Operations.Where(x => x.UserId == r.User).ToListAsync();
            Assert.Contains(events, x => x.Result == OperationResult.Failed && x.OperationType == OperationType.OPERATION_FAILED);
            Assert.Contains(events, x => x.Result == OperationResult.Failed && x.OperationType == (name.EndsWith("json") ? OperationType.JSON_VALIDATED : OperationType.XML_VALIDATED));
            Assert.All(events, x => Assert.DoesNotContain("SECRET_FRAGMENT", x.Details ?? ""));
            using var space = r.Disk.Open(r.User); Assert.Empty(space.Pending());
        }
    }
    [SqlFact]
    public async Task BomAndLineEndingsPreservedForAllFormats()
    {
        var r = await New();
        foreach (var name in new[] { "bom.txt", "bom.json", "bom.xml" })
            foreach (var bom in new[] { false, true })
            {
                var content = name.EndsWith("json") ? "{\r\n\"n\":1\r\n}" : name.EndsWith("xml") ? "<r>\r\nтекст\r\n</r>" : "a\r\nb\n";
                var path = r.Physical(name);
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(bom, true));
                Ok(await r.Files.SynchronizeAsync(name)); var entry = await r.Entry(name);
                var document = await r.Files.ReadAsync(entry.Id!.Value); Ok(document);
                Assert.Equal(content, document.Value!.Content);
                var edited = content.Replace("1", "2").Replace("текст", "изменён").Replace("a", "c");
                Ok(await r.Files.SaveAsync(entry.Id.Value, edited, document.Value.Version));
                Assert.Equal((bom ? Encoding.UTF8.GetPreamble() : []).Concat(Encoding.UTF8.GetBytes(edited)), await File.ReadAllBytesAsync(path));
            }
    }
    [SqlFact]
    public async Task AllFormatsRejectOversizeCorruptUtf8AndMissingOrLockedFiles()
    {
        var r = await New();
        foreach (var name in new[] { "limits.txt", "limits.json", "limits.xml" })
        {
            var path = r.Physical(name);
            using (var stream = new FileStream(path, FileMode.CreateNew)) stream.SetLength(FileRules.TextLimit + 1);
            Ok(await r.Files.SynchronizeAsync(name)); var entry = await r.Entry(name);
            Assert.Equal(FileStatus.LimitExceeded, (await r.Files.ReadAsync(entry.Id!.Value)).Status);
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]); Ok(await r.Files.SynchronizeAsync(name));
            Assert.Equal(FileStatus.Unsupported, (await r.Files.ReadAsync(entry.Id.Value)).Status);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(FileStatus.Conflict, (await r.Files.ReadAsync(entry.Id.Value)).Status);
            File.Delete(path);
            Assert.Equal(FileStatus.NotFound, (await r.Files.ReadAsync(entry.Id.Value)).Status);
        }
    }
    [SqlFact]
    public async Task ForbiddenXmlRejectedAtOpenWhileMalformedDocumentsCanBeCorrected()
    {
        var r = await New();
        foreach (var name in new[] { "broken.json", "broken.xml" })
        {
            await File.WriteAllTextAsync(r.Physical(name), ""); Ok(await r.Files.SynchronizeAsync(name)); var entry = await r.Entry(name);
            var opened = await r.Files.ReadAsync(entry.Id!.Value); Ok(opened); Assert.False(opened.Value!.Validation!.IsValid);
            var correct = name.EndsWith("json") ? "[]" : "<r/>";
            Ok(await r.Files.SaveAsync(entry.Id.Value, correct, opened.Value.Version));
            if (name.EndsWith("xml"))
            {
                await File.WriteAllTextAsync(r.Physical(name), "<!DOCTYPE r SYSTEM 'http://127.0.0.1:1/no'><r/>");
                Ok(await r.Files.SynchronizeAsync(name));
                var denied = await r.Files.ReadAsync(entry.Id.Value); Assert.Equal(FileStatus.InvalidDocument, denied.Status); Assert.Null(denied.Value);
            }
        }
    }
    [SqlFact]
    public async Task StructuredSaveFailuresAndForeignSessionNeverDamageOriginal()
    {
        var r = await New();
        Ok(await r.Files.CreateAsync("", "failure.json", false)); var entry = await r.Entry("failure.json");
        var original = (await r.Files.ReadAsync(entry.Id!.Value)).Value!;
        var failed = r.Build(new FaultStorage(r.Disk, () => throw new IOException("simulated disk failure")));
        Assert.False((await failed.SaveAsync(original.Id, "[1]", original.Version)).Succeeded);
        Assert.Equal("{}", await File.ReadAllTextAsync(r.Physical("failure.json")));
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_missing", ConnectTimeout = 2 };
        var disconnected = r.Build(r.Disk, new FileTransactionStore(new Factory(builder.ConnectionString)));
        Assert.Equal(FileStatus.DatabaseUnavailable, (await disconnected.SaveAsync(original.Id, "[1]", original.Version)).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(r.Physical("failure.json")));
        r.Session.Set(null);
        Assert.Equal(FileStatus.Denied, (await r.Files.ValidateAsync(original.Id, "[1]")).Status);
        Assert.Equal(FileStatus.Denied, (await r.Files.SaveAsync(original.Id, "[1]", original.Version)).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(r.Physical("failure.json")));
    }
    [SqlFact]
    public async Task ForeignStructuredIdsCannotReadValidateReloadOrSave()
    {
        var r = await New();
        var docs = new List<TextDocument>();
        foreach (var name in new[] { "private.json", "private.xml" })
        {
            Ok(await r.Files.CreateAsync("", name, false)); var file = await r.Entry(name);
            docs.Add((await r.Files.ReadAsync(file.Id!.Value)).Value!);
        }
        var other = Guid.NewGuid();
        await using (var db = r.Db.CreateDbContext())
        {
            db.Users.Add(new User { Id = other, Username = "s6-" + Guid.NewGuid().ToString("N"), PasswordHash = "test-only" });
            await db.SaveChangesAsync();
        }
        await r.Paths.PrepareAsync(other); r.Session.Set(other);
        foreach (var doc in docs)
        {
            Assert.Equal(FileStatus.Denied, (await r.Files.ReadAsync(doc.Id)).Status);
            Assert.Equal(FileStatus.Denied, (await r.Files.ReloadAsync(doc.Id)).Status);
            Assert.Equal(FileStatus.Denied, (await r.Files.ValidateAsync(doc.Id, doc.Content)).Status);
            Assert.Equal(FileStatus.Denied, (await r.Files.SaveAsync(doc.Id, doc.Content, doc.Version)).Status);
        }
        await using var inspect = r.Db.CreateDbContext();
        Assert.Contains(await inspect.Operations.Where(x => x.UserId == other).ToListAsync(), x => x.OperationType == OperationType.ACCESS_DENIED && x.FileId == null);
    }
    [SqlFact]
    public async Task StructuredAuditFailureCannotPublishOrReportValidationSuccess()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "audit.xml", false)); var entry = await r.Entry("audit.xml");
        var document = (await r.Files.ReadAsync(entry.Id!.Value)).Value!;
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage06AuditFailure ON Operations AFTER INSERT AS BEGIN THROW 51006, 'Test-only audit failure', 1; END;");
        try
        {
            Assert.Equal(FileStatus.DatabaseUnavailable, (await r.Files.ValidateAsync(document.Id, "<edited/>")).Status);
            Assert.Equal(FileStatus.DatabaseUnavailable, (await r.Files.SaveAsync(document.Id, "<edited/>", document.Version)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage06AuditFailure;"); }
        Assert.Equal(document.Content, await File.ReadAllTextAsync(r.Physical("audit.xml")));
    }
}
