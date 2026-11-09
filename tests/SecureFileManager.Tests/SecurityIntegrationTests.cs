using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;
using SecureFileManager.Core.Security;
using SecureFileManager.Core.Services;
using SecureFileManager.Data;
using SecureFileManager.Data.Identity;
using SecureFileManager.Data.Repositories;
using SecureFileManager.Infrastructure.Windows.FileSystem;

using Xunit.Abstractions;

namespace SecureFileManager.Tests;

[Collection("SQL Server")]
public sealed class SecurityIntegrationTests(SqlDatabaseFixture database, ITestOutputHelper output)
{
    private const string Password = "Stage04 test-only password!";
    private Rig NewRig(string? root = null)
    {
        output.WriteLine("Security test DB: " + database.DatabaseName);
        return new Rig(database.ConnectionString, root);
    }
    private static string Name() => "u-" + Guid.NewGuid().ToString("N");

    [SqlFact]
    public async Task SuccessfulLoginUpgradesOldHashAndStorageRepairRequiresAudit()
    {
        var r = NewRig(); var name = Name(); var id = Guid.NewGuid();
        var old = new PasswordSecurity(100_000).Hash(Password);
        await using (var db = r.Factory.CreateDbContext())
        {
            db.Users.Add(new User { Id = id, Username = name, PasswordHash = old, CreatedAtUtc = r.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.True((await r.Auth.LoginAsync(name, Password)).Succeeded);
        var prepared = await r.Storage.PrepareCurrentAsync();
        Assert.True(prepared.Succeeded); Assert.True(prepared.Created);
        Assert.True(Directory.Exists(prepared.PhysicalPath));
        await using var verify = r.Factory.CreateDbContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.NotEqual(old, user.PasswordHash);
        Assert.Equal(PasswordCheck.Success, r.Passwords.Verify(user.PasswordHash, Password));
        Assert.True(await verify.Operations.AnyAsync(x => x.UserId == id && x.Details == "PERSONAL_STORAGE_REPAIRED"));
        Assert.False((await r.Storage.PrepareCurrentAsync()).Created);

        var missingConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_unavailable", ConnectTimeout = 2 };
        var missingFactory = new Factory(missingConnection.ConnectionString);
        var paths = new PersonalStorage(r.Root);
        var blockedStorage = new UserStorageService(r.Session, new PersonalFileAccessPolicy(), paths, paths,
            new FileRepository(missingFactory), new SecurityAudit(new OperationRepository(missingFactory), r.Clock), new UserRepository(missingFactory));
        Assert.Equal(StorageStatus.InfrastructureError, (await blockedStorage.CheckPathAsync(id, "")).Status);
        Assert.Equal(StorageStatus.InfrastructureError, (await blockedStorage.PrepareCurrentAsync()).Status);
    }

    [SqlFact]
    public async Task RegisterStoresOnlyHashPreparesGuidDirectoryAndRejectsDuplicate()
    {
        var r = NewRig(); var name = Name();
        Assert.True((await r.Registration.RegisterAsync(name, Password, Password)).Succeeded);
        Assert.True(await new DatabaseConnectionCheck(r.Factory).CanConnectAsync());
        Assert.Null(r.Session.Current); // registration is not login
        await using var db = r.Factory.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.Username == name);
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Equal(PasswordCheck.Success, r.Passwords.Verify(user.PasswordHash, Password));
        Assert.True(Directory.Exists(Path.Combine(r.Root, "Users", user.Id.ToString("D"))));
        Assert.Equal(AuthStatus.Conflict, (await r.Registration.RegisterAsync(name.ToUpperInvariant(), Password, Password)).Status);
        Assert.Contains(await db.Operations.Where(x => x.UserId == user.Id).ToListAsync(),
            x => x.OperationType == OperationType.DIRECTORY_CREATED && x.Result == OperationResult.Success);
    }

    [SqlFact]
    public async Task RegistrationValidationAndDirectoryFailureDoNotCreateUsersOrDeleteData()
    {
        var r = NewRig();
        Assert.Equal(AuthStatus.Validation, (await r.Registration.RegisterAsync("ab", Password, Password)).Status);
        Assert.Equal(AuthStatus.Validation, (await r.Registration.RegisterAsync(Name(), "short", "short")).Status);
        Assert.Equal(AuthStatus.Validation, (await r.Registration.RegisterAsync(Name(), Password, "different")).Status);
        Assert.Equal(AuthStatus.Validation, (await r.Registration.RegisterAsync(new string('a', 51), Password, Password)).Status);
        Assert.Equal(AuthStatus.Validation, (await r.Registration.RegisterAsync(Name(), new string('p', 129), new string('p', 129))).Status);
        Directory.CreateDirectory(r.Root);
        var blocked = Path.Combine(r.Root, "blocked");
        await File.WriteAllTextAsync(blocked, "TEST-ONLY sentinel");
        var failed = NewRig(blocked); var name = Name();
        Assert.Equal(AuthStatus.StorageUnavailable, (await failed.Registration.RegisterAsync(name, Password, Password)).Status);
        await using var db = r.Factory.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(x => x.Username == name));
        Assert.Equal("TEST-ONLY sentinel", await File.ReadAllTextAsync(blocked));
        Assert.Contains(await db.Operations.ToListAsync(), x => x.Details == "REGISTRATION_STORAGE_FAILED");
    }

    [SqlFact]
    public async Task ConcurrentCaseInsensitiveRegistrationHasExactlyOneWinner()
    {
        var r = NewRig(); var name = Name();
        var results = await Task.WhenAll(r.Registration.RegisterAsync(name, Password, Password),
            r.Registration.RegisterAsync(name.ToUpperInvariant(), Password, Password));
        Assert.Single(results, x => x.Succeeded);
        Assert.Single(results, x => x.Status == AuthStatus.Conflict);
        await using var db = r.Factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(x => x.Username == name));
    }

    [SqlFact]
    public async Task WrongUnknownInactiveAndLockedLoginsUseSameMessageAndAuditNullableFile()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        var wrong = await r.Auth.LoginAsync(name, "incorrect password");
        var unknownName = Name();
        var unknown = await r.Auth.LoginAsync(unknownName, Password);
        Assert.Equal("Неверный логин или пароль", wrong.Message);
        Assert.Equal(wrong.Message, unknown.Message);
        Assert.Null(r.Session.Current);
        await using (var db = r.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Username == name);
            user.IsActive = false; await db.SaveChangesAsync();
        }
        Assert.Equal(wrong.Message, (await r.Auth.LoginAsync(name, Password)).Message);
        await using (var db = r.Factory.CreateDbContext())
        {
            var item = await db.Operations.SingleAsync(x => x.ActorNameSnapshot == unknownName);
            Assert.Null(item.UserId); Assert.Null(item.FileId);
            Assert.Equal(OperationResult.Failed, item.Result);
            Assert.Equal(DateTimeKind.Utc, item.OccurredAtUtc.Kind);
        }
    }

    [SqlFact]
    public async Task FiveFailuresBlockForFifteenMinutesAndSuccessResets()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        for (var i = 0; i < 5; i++) Assert.Equal(AuthStatus.InvalidCredentials, (await r.Auth.LoginAsync(name, "incorrect password")).Status);
        await using (var db = r.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Username == name);
            Assert.Equal(5, user.FailedLoginCount);
            Assert.Equal(r.Clock.UtcNow.AddMinutes(15), user.LockoutUntilUtc);
        }
        Assert.Equal(AuthStatus.InvalidCredentials, (await r.Auth.LoginAsync(name, Password)).Status);
        r.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True((await r.Auth.LoginAsync(name, Password)).Succeeded);
        await using (var db = r.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Username == name);
            Assert.Equal(0, user.FailedLoginCount); Assert.Null(user.FailedLoginWindowStartUtc); Assert.Null(user.LockoutUntilUtc);
        }
    }

    [SqlFact]
    public async Task FailureWindowSlidesInsteadOfDroppingRecentAttemptsAtFixedBoundary()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        await r.Auth.LoginAsync(name, "incorrect password");
        r.Clock.Advance(TimeSpan.FromMinutes(14));
        for (var i = 0; i < 3; i++) await r.Auth.LoginAsync(name, "incorrect password");
        r.Clock.Advance(TimeSpan.FromMinutes(2));
        await r.Auth.LoginAsync(name, "incorrect password");
        await using (var db = r.Factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Username == name);
            Assert.Equal(4, user.FailedLoginCount); Assert.Null(user.LockoutUntilUtc);
        }
        await r.Auth.LoginAsync(name, "incorrect password");
        Assert.Equal(AuthStatus.InvalidCredentials, (await r.Auth.LoginAsync(name, Password)).Status);
    }

    [SqlFact]
    public async Task ConcurrentFailuresAcrossServiceInstancesDoNotLoseCounterUpdates()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        var attempts = Enumerable.Range(0, 5).Select(_ =>
            new AuthenticationService(r.Store, r.Passwords, new UserSession(), r.Clock).LoginAsync(name, "incorrect password"));
        Assert.All(await Task.WhenAll(attempts), x => Assert.Equal(AuthStatus.InvalidCredentials, x.Status));
        await using var db = r.Factory.CreateDbContext();
        Assert.Equal(5, (await db.Users.SingleAsync(x => x.Username == name)).FailedLoginCount);
    }

    [SqlFact]
    public async Task AccessPolicyRejectsForeignIdPathsAndUnauthenticatedCallsWithoutTouchingFiles()
    {
        var r = NewRig(); var a = Name(); var b = Name();
        await r.Registration.RegisterAsync(a, Password, Password);
        await r.Registration.RegisterAsync(b, Password, Password);
        Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckPathAsync(Guid.NewGuid(), "")).Status);
        Assert.True((await r.Auth.LoginAsync(a, Password)).Succeeded);
        var owner = r.Session.Current!.Id;
        User foreign;
        await using (var db = r.Factory.CreateDbContext()) foreign = await db.Users.SingleAsync(x => x.Username == b);
        var foreignFile = new FileRecord { OwnerId = foreign.Id, Name = "sentinel.txt", RelativePath = "sentinel.txt" };
        Assert.True((await new FileRepository(r.Factory).AddAsync(foreignFile)).IsSuccess);
        var sentinel = Path.Combine(r.Root, "Users", foreign.Id.ToString("D"), "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "TEST-ONLY foreign data");
        Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckFileAsync(foreignFile.Id)).Status);
        Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckPathAsync(foreign.Id, "")).Status);
        foreach (var bad in new[] { "../outside.txt", "/outside.txt", @"C:\outside.txt", @"..\other", @"\\server\share", "NUL.txt" })
            Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckPathAsync(owner, bad)).Status);
        Assert.True((await r.Storage.CheckPathAsync(owner, "")).Succeeded);
        Assert.Equal("TEST-ONLY foreign data", await File.ReadAllTextAsync(sentinel));
        await using var verify = r.Factory.CreateDbContext();
        var denied = await verify.Operations.Where(x => x.UserId == owner && x.OperationType == OperationType.ACCESS_DENIED).ToListAsync();
        Assert.NotEmpty(denied);
        Assert.All(denied, item => { Assert.Null(item.FileId); Assert.Null(item.TargetPathSnapshot); Assert.Equal("ACCESS_DENIED", item.Details); });
    }

    [SqlFact]
    public async Task JunctionAndHardLinkCannotEscapePersonalRoot()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        await r.Auth.LoginAsync(name, Password);
        var owner = r.Session.Current!.Id;
        var root = (await r.Storage.PrepareCurrentAsync()).PhysicalPath!;
        var outside = Path.Combine(r.Root, "TEST-ONLY-outside");
        Directory.CreateDirectory(outside);
        var source = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(source, "TEST-ONLY preserved data");
        var junction = Path.Combine(root, "junction");
        CreateJunction(junction, outside);
        Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckPathAsync(owner, "junction/sentinel.txt")).Status);
        var hardLink = Path.Combine(root, "hard-link.txt");
        Assert.True(CreateHardLink(hardLink, source, IntPtr.Zero), "CreateHardLink failed in isolated test root.");
        Assert.Equal(StorageStatus.Denied, (await r.Storage.CheckPathAsync(owner, "hard-link.txt")).Status);
        Assert.Equal("TEST-ONLY preserved data", await File.ReadAllTextAsync(source));
    }

    [SqlFact]
    public async Task AuditFailureRollsBackLoginAndLogoutStillClearsSession()
    {
        var r = NewRig(); var name = Name();
        await r.Registration.RegisterAsync(name, Password, Password);
        await r.Auth.LoginAsync(name, "incorrect password");
        await using var db = r.Factory.CreateDbContext();
        await InstallFailingAuditTrigger(db);
        try
        {
            Assert.Equal(AuthStatus.InfrastructureError, (await r.Auth.LoginAsync(name, Password)).Status);
            Assert.Null(r.Session.Current);
            Assert.Equal(1, (await db.Users.AsNoTracking().SingleAsync(x => x.Username == name)).FailedLoginCount);
        }
        finally { await RemoveFailingAuditTrigger(db); }
        Assert.True((await r.Auth.LoginAsync(name, Password)).Succeeded);
        await InstallFailingAuditTrigger(db);
        try
        {
            Assert.Equal(AuthStatus.InfrastructureError, (await r.Auth.LogoutAsync()).Status);
            Assert.Null(r.Session.Current);
        }
        finally { await RemoveFailingAuditTrigger(db); }
        Assert.True((await r.Auth.LoginAsync(name, Password)).Succeeded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Auth.LogoutAsync(cancellation.Token));
        Assert.Null(r.Session.Current);
    }

    [SqlFact]
    public async Task RegistrationAuditFailureKeepsExistingFilesAndDoesNotCommitUser()
    {
        var r = NewRig(); var name = Name();
        Directory.CreateDirectory(r.Root);
        var sentinel = Path.Combine(r.Root, "TEST-ONLY-marker.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        await using var db = r.Factory.CreateDbContext();
        await InstallFailingAuditTrigger(db);
        try
        {
            Assert.Equal(AuthStatus.InfrastructureError, (await r.Registration.RegisterAsync(name, Password, Password)).Status);
            Assert.False(await db.Users.AnyAsync(x => x.Username == name));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.NotEmpty(Directory.GetDirectories(Path.Combine(r.Root, "Users")));
        }
        finally { await RemoveFailingAuditTrigger(db); }
    }

    [SqlFact]
    public async Task MissingDatabaseCannotAuthenticateRegisterOrAuthorizeStorage()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString)
        { InitialCatalog = database.DatabaseName + "_unavailable", ConnectTimeout = 2 };
        var r = new Rig(builder.ConnectionString, null);
        Assert.Equal(AuthStatus.InfrastructureError, (await r.Auth.LoginAsync(Name(), Password)).Status);
        Assert.Equal(AuthStatus.InfrastructureError, (await r.Registration.RegisterAsync(Name(), Password, Password)).Status);
        Assert.Null(r.Session.Current);
        Assert.False(Directory.Exists(r.Root));
        Assert.False(await new DatabaseConnectionCheck(r.Factory).CanConnectAsync());
    }

    private static Task InstallFailingAuditTrigger(AppDbContext db) => db.Database.ExecuteSqlRawAsync(
        "CREATE TRIGGER [Stage04TestAuditFailure] ON [Operations] AFTER INSERT AS BEGIN THROW 51004, 'TEST-ONLY audit failure', 1; END;");
    private static Task RemoveFailingAuditTrigger(AppDbContext db) => db.Database.ExecuteSqlRawAsync("DROP TRIGGER [Stage04TestAuditFailure];");

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan time) => UtcNow += time;
    }
    private sealed class Factory(string connection) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
    }
    private sealed class Rig
    {
        public Factory Factory { get; }
        public TestClock Clock { get; } = new();
        public PasswordSecurity Passwords { get; } = new();
        public UserSession Session { get; } = new();
        public IdentityStore Store { get; }
        public RegistrationService Registration { get; }
        public AuthenticationService Auth { get; }
        public UserStorageService Storage { get; }
        public string Root { get; }
        public Rig(string connection, string? root)
        {
            Root = root ?? Path.Combine(Environment.GetEnvironmentVariable("SFM_STORAGE_TEST_ROOT") ?? Path.GetTempPath(),
                "SecureFileManager-stage04-tests", Guid.NewGuid().ToString("N"), "Storage");
            Factory = new(connection); Store = new(Factory);
            var provisioner = new PersonalStorage(Root);
            var audit = new SecurityAudit(new OperationRepository(Factory), Clock);
            Registration = new(Store, Passwords, provisioner, Clock, audit);
            Auth = new(Store, Passwords, Session, Clock);
            Storage = new(Session, new PersonalFileAccessPolicy(), provisioner, provisioner, new FileRepository(Factory), audit, new UserRepository(Factory));
        }
    }

    internal static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, true))
        {
            writer.Write(0xA0000003u);
            writer.Write((ushort)(8 + substitute.Length + print.Length + 4)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substitute.Length);
            writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)print.Length);
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
        }
        using var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid, "Cannot open test junction.");
        var bytes = data.ToArray();
        Assert.True(DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "Cannot create junction in isolated test directory.");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
