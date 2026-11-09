using SecureFileManager.Cli;
using System.IO;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Journal;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Services;
using SecureFileManager.Data.Identity;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Data.Files;


namespace SecureFileManager.Tests;

public sealed partial class FileServiceTests
{
    [SqlFact]
    public async Task EmptyFileTransactionDoesNotClaimAuditWriteAvailability()
    {
        var r = await New(); var health = new AuditHealth(new SystemClock());
        var store = new FileTransactionStore(r.Db, health);
        Assert.True(await store.ExecuteAsync(r.User, _ => Task.FromResult(true), default));
        Assert.Equal(AuditState.Unknown, health.Snapshot.State);
        var files = new FileService(r.Session, new PersonalFileAccessPolicy(), store, r.Disk,
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock(), health), new SystemClock());
        Ok(await files.RecoverAsync()); Assert.Equal(AuditState.Unknown, health.Snapshot.State);
        Ok(await files.CreateAsync("", "audited.txt", false)); Assert.Equal(AuditState.Active, health.Snapshot.State);
    }
    private static JournalService Journal(Rig r, AuditHealth? health = null, IOperationRepository? repository = null) =>
        new(r.Session, new UserRepository(r.Db), repository ?? new OperationRepository(r.Db), health ?? new(new SystemClock()), new AuditProbe(r.Db));

    [SqlFact]
    public async Task JournalServerFiltersPagesSortsAndNeverIncludesForeignOrSystemEvents()
    {
        var r = await New(); var other = await New();
        await using var db = r.Db.CreateDbContext();
        var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 7; i++) db.Operations.Add(new Operation { UserId = r.User, ActorNameSnapshot = "test-only",
            OccurredAtUtc = time.AddMinutes(i), OperationType = OperationType.FILE_READ, Result = i % 2 == 0 ? OperationResult.Success : OperationResult.Failed,
            TargetPathSnapshot = "name' OR 1=1--.txt", Details = "TEXT_READ" });
        db.Operations.Add(new Operation { UserId = other.User, OperationType = OperationType.FILE_READ, Result = OperationResult.Success });
        db.Operations.Add(new Operation { OperationType = OperationType.LOGIN_FAILED, Result = OperationResult.Failed });
        await db.SaveChangesAsync();
        var journal = Journal(r);
        var first = await journal.GetAsync(new(PageSize: 3)); Assert.True(first.IsSuccess); Assert.True(first.Value!.HasMore);
        var second = (await journal.GetAsync(new(Page: 1, PageSize: 3))).Value!;
        Assert.Empty(first.Value.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.All(first.Value.Items.Concat(second.Items), x => Assert.Equal(r.User, x.UserId));
        Assert.True(first.Value.Items[0].OccurredAtUtc > second.Items[0].OccurredAtUtc);
        var filtered = (await journal.GetAsync(new(OperationType.FILE_READ, OperationResult.Success, time.AddMinutes(1), time.AddMinutes(6), "OR 1=1", false))).Value!;
        Assert.Equal(2, filtered.Items.Count); Assert.True(filtered.Items[0].OccurredAtUtc < filtered.Items[1].OccurredAtUtc);
        Assert.Empty((await journal.GetAsync(new(Search: "%_' OR 1=1 --"))).Value!.Items);
        Assert.All(first.Value.Items, x => Assert.Equal(DateTimeKind.Utc, x.OccurredAtUtc.Kind));
        Assert.Equal(3, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ('Users','Files','Operations')").SingleAsync());
    }
    [SqlFact]
    public async Task JournalDeletedHistorySurvivesAndInvalidQueriesDoNotBypassSession()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "history.txt", false)); var file = await r.Entry("history.txt");
        Ok(await r.Files.DeleteAsync(file.Id!.Value));
        var journal = Journal(r); var history = (await journal.GetAsync(new(Search: "history.txt"))).Value!;
        Assert.Contains(history.Items, x => x.OperationType == OperationType.FILE_CREATED);
        Assert.Contains(history.Items, x => x.OperationType == OperationType.FILE_DELETED);
        foreach (var query in new[] { new JournalQuery(Type: (OperationType)999), new JournalQuery(Page: -1), new JournalQuery(PageSize: 501),
            new JournalQuery(Search: new string('x', 201)), new JournalQuery(FromUtc: DateTime.Now),
            new JournalQuery(FromUtc: DateTime.UtcNow, UntilUtc: DateTime.UtcNow.AddDays(-1)) })
            Assert.Equal(RepositoryStatus.Validation, (await journal.GetAsync(query)).Status);
        r.Session.Set(null); Assert.Equal(RepositoryStatus.Unauthorized, (await journal.GetAsync(new())).Status);
        r.Session.Set(r.User);
        await using var db = r.Db.CreateDbContext(); var user = await db.Users.SingleAsync(x => x.Id == r.User); user.IsActive = false; await db.SaveChangesAsync();
        Assert.Equal(RepositoryStatus.Unauthorized, (await journal.GetAsync(new())).Status);
    }
    private sealed class DelayedOperations(IOperationRepository inner, Action switchUser) : IOperationRepository
    {
        public Task<RepositoryResult<Operation>> AddAsync(Operation item, CancellationToken ct = default) => inner.AddAsync(item, ct);
        public Task<RepositoryResult<IReadOnlyList<Operation>>> GetForUserAsync(Guid id, int take = 100, CancellationToken cancellationToken = default) => inner.GetForUserAsync(id, take, cancellationToken);
        public async Task<RepositoryResult<JournalPage>> QueryAsync(Guid user, JournalQuery query, CancellationToken ct = default)
        { var result = await inner.QueryAsync(user, query, ct); switchUser(); return result; }
    }
    [SqlFact]
    public async Task JournalDiscardsInFlightResultAfterSessionSwitch()
    {
        var r = await New(); var other = await New();
        var service = Journal(r, repository: new DelayedOperations(new OperationRepository(r.Db), () => r.Session.Set(other.User)));
        Assert.Equal(RepositoryStatus.Unauthorized, (await service.GetAsync(new())).Status);
    }
    [SqlFact]
    public async Task AuditProbeRollsBackAndDetectsInsertFailureThenRecovers()
    {
        var r = await New(); var health = new AuditHealth(new SystemClock()); var journal = Journal(r, health);
        await using var db = r.Db.CreateDbContext(); var count = await db.Operations.CountAsync();
        await journal.CheckHealthAsync(); Assert.Equal(AuditState.Active, health.Snapshot.State);
        Assert.Equal(count, await db.Operations.CountAsync());
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage08ProbeFailure ON Operations AFTER INSERT AS BEGIN THROW 51008, 'Test-only failure', 1; END;");
        try { await journal.CheckHealthAsync(); Assert.Equal(AuditState.Unavailable, health.Snapshot.State); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage08ProbeFailure;"); }
        Assert.Equal(count, await db.Operations.CountAsync());
        await journal.CheckHealthAsync(); Assert.Equal(AuditState.Active, health.Snapshot.State);
    }
    [SqlFact]
    public async Task RealAuditFailureUpdatesHealthAndDeleteRestoresOriginalFile()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "keep.txt", false)); var file = await r.Entry("keep.txt");
        var health = new AuditHealth(new SystemClock()); var audit = new SecurityAudit(new OperationRepository(r.Db), new SystemClock(), health);
        var service = new FileService(r.Session, new PersonalFileAccessPolicy(), new FileTransactionStore(r.Db, health), r.Disk, audit, new SystemClock());
        await using var db = r.Db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER Stage08WriteFailure ON Operations AFTER INSERT AS BEGIN THROW 51008, 'Test-only failure', 1; END;");
        try { Assert.Equal(FileStatus.DatabaseUnavailable, (await service.DeleteAsync(file.Id!.Value)).Status); Assert.Equal(AuditState.Unavailable, health.Snapshot.State); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Stage08WriteFailure;"); }
        Assert.True(File.Exists(r.Physical("keep.txt"))); Assert.True(await db.Files.AnyAsync(x => x.Id == file.Id));
        Assert.False(await db.Operations.AnyAsync(x => x.FileId == file.Id && x.OperationType == OperationType.FILE_DELETED));
        using (var space = r.Disk.Open(r.User)) Assert.Empty(space.Pending());
        Ok(await service.DeleteAsync(file.Id!.Value));
    }
    [SqlFact]
    public async Task DiagnosisReportsUntrackedMissingModifiedDeletedAndAuditMismatchWithoutChangingData()
    {
        var r = await New();
        foreach (var name in new[] { "missing.txt", "changed.txt", "deleted.txt", "reused.txt" }) Ok(await r.Files.CreateAsync("", name, false));
        var missing = await r.Entry("missing.txt"); var deleted = await r.Entry("deleted.txt"); var reused = await r.Entry("reused.txt");
        File.Delete(r.Physical("missing.txt")); await File.WriteAllTextAsync(r.Physical("changed.txt"), "external change");
        Ok(await r.Files.DeleteAsync(deleted.Id!.Value)); await File.WriteAllTextAsync(r.Physical("deleted.txt"), "retained external");
        Ok(await r.Files.DeleteAsync(reused.Id!.Value)); Ok(await r.Files.CreateAsync("", "reused.txt", false));
        await File.WriteAllTextAsync(r.Physical("unknown.txt"), "unknown preserved");
        await using var db = r.Db.CreateDbContext();
        var now = DateTime.UtcNow;
        db.Files.Add(new FileRecord { OwnerId = r.User, Name = "no-audit.txt", RelativePath = "no-audit.txt", CreatedAtUtc = now, ModifiedAtUtc = now });
        await db.SaveChangesAsync(); var before = await db.Files.IgnoreQueryFilters().CountAsync(x => x.OwnerId == r.User);
        var report = await ((IStorageDiagnostics)r.Files).DiagnoseAsync(); Ok(report); Assert.True(report.Value!.Complete);
        Assert.Contains(report.Value.Issues, x => x.Code == "UNTRACKED" && x.Path == "unknown.txt");
        Assert.Contains(report.Value.Issues, x => x.Code == "MISSING" && x.Path == "missing.txt");
        Assert.Contains(report.Value.Issues, x => x.Code == "METADATA_CHANGED" && x.Path == "changed.txt");
        Assert.Contains(report.Value.Issues, x => x.Code == "DELETED_PRESENT" && x.Path == "deleted.txt");
        Assert.Contains(report.Value.Issues, x => x.Code == "AUDIT_METADATA_MISMATCH" && x.Path == "no-audit.txt");
        Assert.DoesNotContain(report.Value.Issues, x => x.Code == "DELETED_PRESENT" && x.Path == "reused.txt");
        Assert.Equal(before, await db.Files.IgnoreQueryFilters().CountAsync(x => x.OwnerId == r.User));
        Assert.True(await db.Files.AnyAsync(x => x.Id == missing.Id));
        Assert.Equal("unknown preserved", await File.ReadAllTextAsync(r.Physical("unknown.txt")));
        Assert.Equal("retained external", await File.ReadAllTextAsync(r.Physical("deleted.txt")));
    }
    [SqlFact]
    public async Task DiagnosisFindsPendingAndUnknownStagingWithoutCleaningIt()
    {
        var r = await New();
        using (var space = r.Disk.Open(r.User)) { var plan = space.Stage(Guid.NewGuid(), ChangeKind.Create, "pending.txt"); space.Publish(plan); }
        var report = await ((IStorageDiagnostics)r.Files).DiagnoseAsync(); Ok(report);
        Assert.Contains(report.Value!.Issues, x => x.Code == "PENDING_OPERATION"); Assert.True(File.Exists(r.Physical("pending.txt")));
        Ok(await r.Files.RecoverAsync());
        var staging = r.Physical(".sfm-internal/unknown.tmp"); await File.WriteAllTextAsync(staging, "preserve forensic data");
        report = await ((IStorageDiagnostics)r.Files).DiagnoseAsync(); Ok(report);
        Assert.Contains(report.Value!.Issues, x => x.Code == "INCOMPLETE_STAGING"); Assert.Equal("preserve forensic data", await File.ReadAllTextAsync(staging));
        Assert.Equal(FileStatus.RecoveryRequired, (await r.Files.CreateAsync("", "blocked.txt", false)).Status);
    }
    [SqlFact]
    public async Task DiagnosisRecognizesExtractedTreeAuditAndDoesNotFlagNewZipChildren()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "note.txt", false)); var file = await r.Entry("note.txt");
        var zip = (IArchiveService)r.Files; Ok(await zip.CreateArchiveAsync([file.Id!.Value], "", "backup.zip"));
        var archive = await r.Entry("backup.zip"); var preview = (await zip.PreviewArchiveAsync(archive.Id!.Value)).Value!;
        Ok(await zip.ExtractArchiveAsync(archive.Id.Value, "", "result", preview.Version));
        var diagnosis = await ((IStorageDiagnostics)r.Files).DiagnoseAsync(); Ok(diagnosis); Assert.Empty(diagnosis.Value!.Issues);
    }
    [SqlFact]
    public async Task ConcurrentSavesAndDeleteDuringLockedReadPreserveData()
    {
        var r = await New(); Ok(await r.Files.CreateAsync("", "race.txt", false)); var file = await r.Entry("race.txt");
        var document = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        var results = await Task.WhenAll(r.Files.SaveAsync(file.Id.Value, "first", document.Version), r.Build(r.Disk).SaveAsync(file.Id.Value, "second", document.Version));
        Assert.Single(results, x => x.Succeeded); Assert.Single(results, x => x.Status == FileStatus.Conflict);
        var before = await File.ReadAllTextAsync(r.Physical("race.txt"));
        using (var locked = new FileStream(r.Physical("race.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(FileStatus.Conflict, (await r.Files.DeleteAsync(file.Id.Value)).Status);
        Assert.Equal(before, await File.ReadAllTextAsync(r.Physical("race.txt")));
    }
    [SqlFact]
    public async Task JournalAndDiagnosticsMissingDatabaseFailClosedAndHealthIsUnavailable()
    {
        var r = await New(); var missing = new Factory(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_missing", ConnectTimeout = 2 }.ConnectionString);
        var health = new AuditHealth(new SystemClock());
        var journal = new JournalService(r.Session, new UserRepository(missing), new OperationRepository(missing), health, new AuditProbe(missing));
        Assert.Equal(RepositoryStatus.InfrastructureError, (await journal.GetAsync(new())).Status);
        Assert.Equal(AuditState.Unavailable, health.Snapshot.State);
        var files = r.Build(r.Disk, new FileTransactionStore(missing, health));
        Assert.Equal(FileStatus.DatabaseUnavailable, (await ((IStorageDiagnostics)files).DiagnoseAsync()).Status);
    }
    [SqlFact]
    public async Task SqlPayloadFileNameIsLiteralAndAuditContainsNoDocumentPayloadOrExternalSource()
    {
        var r = await New(); const string name = "quote' OR 1=1--.txt"; const string secret = "DOCUMENT_SECRET_STAGE08";
        Ok(await r.Files.CreateAsync("", name, false)); var file = await r.Entry(name); var document = (await r.Files.ReadAsync(file.Id!.Value)).Value!;
        Ok(await r.Files.SaveAsync(file.Id.Value, secret, document.Version));
        var events = (await Journal(r).GetAsync(new(Search: "OR 1=1"))).Value!.Items;
        Assert.NotEmpty(events);
        Assert.All(events, x => { Assert.DoesNotContain(secret, x.Details ?? ""); Assert.DoesNotContain(r.Root, x.Details ?? ""); Assert.DoesNotContain(secret, AuditListItem.From(x).Description); });
        Assert.Equal(secret, (await r.Files.ReadAsync(file.Id.Value)).Value!.Content);
    }
    [SqlFact]
    public async Task JournalRedactsUnrecognizedHistoricalDetailsAndAuditsDeniedRead()
    {
        var r = await New();
        await using var db = r.Db.CreateDbContext();
        db.Operations.Add(new Operation { UserId = r.User, Details = "LEGACY_SECRET_PAYLOAD", OperationType = OperationType.OPERATION_FAILED,
            Result = OperationResult.Failed }); await db.SaveChangesAsync();
        var health = new AuditHealth(new SystemClock());
        var service = new JournalService(r.Session, new UserRepository(r.Db), new OperationRepository(r.Db), health, new AuditProbe(r.Db),
            new SecurityAudit(new OperationRepository(r.Db), new SystemClock(), health));
        Assert.Null(Assert.Single((await service.GetAsync(new())).Value!.Items).Details);
        r.Session.Set(null); Assert.Equal(RepositoryStatus.Unauthorized, (await service.GetAsync(new())).Status);
        Assert.True(await db.Operations.AnyAsync(x => x.UserId == null && x.OperationType == OperationType.ACCESS_DENIED && x.Details == "JOURNAL_ACCESS_DENIED"));
    }
    [SqlFact]
    public async Task SqlInjectionLoginIsLiteralAndLockoutCodeRevealsNoPassword()
    {
        var r = await New(); var session = new UserSession(); var clock = new SystemClock();
        var passwords = new Core.Security.PasswordSecurity(1000); // test-only cost; production remains 600000
        var audit = new SecurityAudit(new OperationRepository(r.Db), clock);
        var registration = new RegistrationService(new IdentityStore(r.Db), passwords, r.Paths, clock, audit);
        var name = "a-" + Guid.NewGuid().ToString("N"); const string password = "Stage08 TEST ONLY password!";
        Assert.True((await registration.RegisterAsync(name, password, password)).Succeeded);
        var auth = new AuthenticationService(new IdentityStore(r.Db), passwords, session, clock);
        Assert.Equal(AuthStatus.InvalidCredentials, (await auth.LoginAsync("x'OR'1'='1--", password)).Status);
        Assert.Null(session.Current);
        for (var i = 0; i < 5; i++) Assert.Equal(AuthStatus.InvalidCredentials, (await auth.LoginAsync(name, "wrong test password")).Status);
        await using var db = r.Db.CreateDbContext(); var id = await db.Users.Where(x => x.Username == name).Select(x => x.Id).SingleAsync();
        Assert.False(await db.Operations.AnyAsync(x => x.UserId == null && x.ActorNameSnapshot == "x'OR'1'='1--"));
        var events = await db.Operations.Where(x => x.UserId == id).ToListAsync();
        Assert.Contains(events, x => x.OperationType == OperationType.LOGIN_FAILED && x.Details == "LOGIN_LOCKED");
        Assert.All(events, x => { Assert.DoesNotContain(password, x.Details ?? ""); Assert.DoesNotContain("wrong test password", x.Details ?? ""); });
    }
    [SqlFact]
    public async Task DiagnosisDoesNotTraverseJunctionAndForeignSessionCannotReachPreviousRoot()
    {
        var r = await New(); var foreign = await New();
        var outside = Path.Combine(Path.GetDirectoryName(r.Root)!, "outside-check"); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "preserved");
        SecurityIntegrationTests.CreateJunction(r.Physical("link"), outside);
        var report = await ((IStorageDiagnostics)r.Files).DiagnoseAsync(); Ok(report);
        Assert.Contains(report.Value!.Issues, x => x.Code == "UNSAFE_OBJECT");
        Assert.DoesNotContain(report.Value.Issues, x => x.Path?.Contains("sentinel") == true);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")));
        r.Session.Set(foreign.User); Assert.Equal(FileStatus.Unavailable, (await ((IStorageDiagnostics)r.Files).DiagnoseAsync()).Status);
        r.Session.Set(null); Assert.Equal(FileStatus.Denied, (await ((IStorageDiagnostics)r.Files).DiagnoseAsync()).Status);
    }
}
