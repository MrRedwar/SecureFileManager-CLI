using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;
using SecureFileManager.Data;
using SecureFileManager.Data.Repositories;
using Xunit.Abstractions;

namespace SecureFileManager.Tests;

[Collection("SQL Server")]
public sealed class PersistenceTests(SqlDatabaseFixture fixture, ITestOutputHelper output)
{
    private static User NewUser(string name = "test-user") => new()
    {
        Username = name,
        // Random test-only bytes, not a real user's password or a production credential.
        PasswordHash = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64))
    };

    private static FileRecord NewFile(Guid ownerId, string name = "note.txt")
    {
        var now = DateTime.UtcNow;
        return new() { OwnerId = ownerId, Name = name, RelativePath = name, Extension = ".txt", CreatedAtUtc = now, ModifiedAtUtc = now };
    }

    [SqlFact]
    public async Task MetadataCrudSoftDeletePreservesAuditAndAllowsPathReuse()
    {
        output.WriteLine("Isolated SQL database: " + fixture.DatabaseName);
        await using var scope = await fixture.BeginAsync();
        var users = new UserRepository(scope);
        var files = new FileRepository(scope);
        var audit = new OperationRepository(scope);
        var user = NewUser();
        Assert.True((await users.AddAsync(user)).IsSuccess);
        var file = NewFile(user.Id);
        Assert.True((await files.AddAsync(file)).IsSuccess);
        Assert.Equal(DateTimeKind.Utc, (await files.GetByIdAsync(user.Id, file.Id)).Value!.CreatedAtUtc.Kind);
        Assert.Equal(RepositoryStatus.NotFound, (await files.GetByIdAsync(Guid.NewGuid(), file.Id)).Status);
        Assert.Empty((await files.ListAsync(Guid.NewGuid())).Value!);
        file.SizeBytes = 123;
        file.ModifiedAtUtc = DateTime.UtcNow;
        var changed = await files.UpdateAsync(file);
        Assert.True(changed.IsSuccess);
        Assert.Equal(123, (await files.GetByPathAsync(user.Id, "NOTE.TXT")).Value!.SizeBytes);
        var operation = new Operation { UserId = user.Id, FileId = file.Id, OperationType = OperationType.FILE_UPDATED,
            Result = OperationResult.Success, TargetPathSnapshot = file.RelativePath };
        Assert.True((await audit.AddAsync(operation)).IsSuccess);
        Assert.True((await files.SoftDeleteAsync(user.Id, file.Id, changed.Value!.RowVersion, DateTime.UtcNow)).IsSuccess);
        Assert.Equal(RepositoryStatus.NotFound, (await files.GetByIdAsync(user.Id, file.Id)).Status);
        Assert.Empty((await files.ListAsync(user.Id)).Value!);
        var history = (await audit.GetForUserAsync(user.Id)).Value!;
        Assert.Equal(file.Id, Assert.Single(history).FileId);
        await using var db = scope.CreateDbContext();
        var deleted = await db.Files.IgnoreQueryFilters().SingleAsync(x => x.Id == file.Id);
        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAtUtc);
        Assert.True((await files.AddAsync(NewFile(user.Id))).IsSuccess);
        Assert.Equal(2, await db.Files.IgnoreQueryFilters().CountAsync(x => x.OwnerId == user.Id));
    }

    [SqlFact]
    public async Task UsernameUniqueCaseInsensitiveAndInputIsParameterized()
    {
        await using var scope = await fixture.BeginAsync();
        var users = new UserRepository(scope);
        Assert.True((await users.AddAsync(NewUser("TestUser"))).IsSuccess);
        Assert.Equal(RepositoryStatus.Conflict, (await users.AddAsync(NewUser("testuser"))).Status);
        Assert.True((await users.GetByUsernameAsync("TESTUSER")).IsSuccess);
        Assert.Equal(RepositoryStatus.NotFound, (await users.GetByUsernameAsync("' OR 1=1 --")).Status);
    }

    [SqlFact]
    public async Task SqlForeignKeysChecksAndActivePathUniquenessAreEnforced()
    {
        await using var scope = await fixture.BeginAsync();
        var files = new FileRepository(scope);
        Assert.Equal(RepositoryStatus.Validation, (await files.AddAsync(NewFile(Guid.NewGuid()))).Status);
        var user = NewUser();
        Assert.True((await new UserRepository(scope).AddAsync(user)).IsSuccess);
        Assert.True((await files.AddAsync(NewFile(user.Id))).IsSuccess);
        Assert.Equal(RepositoryStatus.Conflict, (await files.AddAsync(NewFile(user.Id, "NOTE.TXT"))).Status);
        var secondUser = NewUser("other-user");
        Assert.True((await new UserRepository(scope).AddAsync(secondUser)).IsSuccess);
        Assert.True((await files.AddAsync(NewFile(secondUser.Id))).IsSuccess);
        await using var db = scope.CreateDbContext();
        // Raw parameterized SQL bypasses context validation to test actual SQL CHECK/FK constraints.
        var countError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Users] SET [FailedLoginCount] = {-1} WHERE [Id] = {user.Id}"));
        Assert.Equal(547, countError.Number);
        var fkError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Files] SET [OwnerId] = {Guid.NewGuid()} WHERE [OwnerId] = {user.Id}"));
        Assert.Equal(547, fkError.Number);
        var sizeError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Files] SET [SizeBytes] = {-1L} WHERE [OwnerId] = {user.Id}"));
        Assert.Equal(547, sizeError.Number);
    }

    [SqlFact]
    public async Task NullableFileAndUserAuditEventsPersistAndAreNotLeakedToOtherUsers()
    {
        await using var scope = await fixture.BeginAsync();
        var user = NewUser();
        await new UserRepository(scope).AddAsync(user);
        var audit = new OperationRepository(scope);
        var known = new Operation { UserId = user.Id, OperationType = OperationType.LOGIN_SUCCESS, Result = OperationResult.Success };
        var unknown = new Operation { ActorNameSnapshot = "unknown-test", OperationType = OperationType.LOGIN_FAILED, Result = OperationResult.Failed };
        Assert.True((await audit.AddAsync(known)).IsSuccess);
        Assert.True((await audit.AddAsync(unknown)).IsSuccess);
        Assert.Null(Assert.Single((await audit.GetForUserAsync(user.Id)).Value!).FileId);
        Assert.Empty((await audit.GetForUserAsync(Guid.NewGuid())).Value!);
        await using var db = scope.CreateDbContext();
        var stored = await db.Operations.SingleAsync(x => x.Id == unknown.Id);
        Assert.Null(stored.FileId);
        Assert.Null(stored.UserId);
        Assert.Equal(DateTimeKind.Utc, stored.OccurredAtUtc.Kind);
        Assert.Equal(RepositoryStatus.Validation, (await audit.AddAsync(new Operation
            { UserId = Guid.NewGuid(), OperationType = OperationType.LOGOUT, Result = OperationResult.Success })).Status);
    }

    [SqlFact]
    public async Task WrongOwnerAndStaleRowVersionCannotModifyMetadata()
    {
        await using var scope = await fixture.BeginAsync();
        var user = NewUser();
        await new UserRepository(scope).AddAsync(user);
        var files = new FileRepository(scope);
        var file = NewFile(user.Id);
        await files.AddAsync(file);
        var stale = (await files.GetByIdAsync(user.Id, file.Id)).Value!;
        var wrongOwner = NewFile(Guid.NewGuid());
        wrongOwner.Id = file.Id;
        Assert.Equal(RepositoryStatus.NotFound, (await files.UpdateAsync(wrongOwner)).Status);
        Assert.Equal(RepositoryStatus.NotFound, (await files.SoftDeleteAsync(wrongOwner.OwnerId, file.Id, file.RowVersion, DateTime.UtcNow)).Status);
        file.SizeBytes = 7;
        file.ModifiedAtUtc = DateTime.UtcNow;
        Assert.True((await files.UpdateAsync(file)).IsSuccess);
        stale.SizeBytes = 9;
        Assert.Equal(RepositoryStatus.Conflict, (await files.UpdateAsync(stale)).Status);
        Assert.Equal(RepositoryStatus.Conflict, (await files.SoftDeleteAsync(user.Id, file.Id, stale.RowVersion, DateTime.UtcNow)).Status);
        Assert.Equal(7, (await files.GetByIdAsync(user.Id, file.Id)).Value!.SizeBytes);
        Assert.Equal(RepositoryStatus.Validation, (await new OperationRepository(scope).AddAsync(new Operation
            { UserId = wrongOwner.OwnerId, FileId = file.Id })).Status);
    }

    [SqlFact]
    public async Task ContextRemoveSoftDeletesFileAndAuditIsAppendOnly()
    {
        await using var scope = await fixture.BeginAsync();
        var user = NewUser();
        await new UserRepository(scope).AddAsync(user);
        var file = NewFile(user.Id);
        await new FileRepository(scope).AddAsync(file);
        var operation = new Operation { UserId = user.Id, FileId = file.Id };
        await new OperationRepository(scope).AddAsync(operation);
        await using (var db = scope.CreateDbContext())
        {
            db.Files.Remove((await db.Files.SingleAsync(x => x.Id == file.Id)));
            await db.SaveChangesAsync();
            Assert.Empty(await db.Files.Where(x => x.OwnerId == user.Id).ToListAsync());
            Assert.True((await db.Files.IgnoreQueryFilters().SingleAsync(x => x.Id == file.Id)).IsDeleted);
            Assert.Single(await db.Operations.Where(x => x.UserId == user.Id).ToListAsync());
        }
        await using (var db = scope.CreateDbContext())
        {
            db.Operations.Remove(await db.Operations.SingleAsync(x => x.Id == operation.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        }
        await using (var db = scope.CreateDbContext())
            Assert.Single(await db.Operations.Where(x => x.UserId == user.Id).ToListAsync());
        await using (var db = scope.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM [Files] WHERE [Id] = {file.Id}"));
            Assert.Equal(547, error.Number); // FK refuses hard-delete; history cannot cascade away.
        }
    }

    [SqlFact]
    public async Task UnavailableDatabaseReturnsSafeInfrastructureFailure()
    {
        var text = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = fixture.DatabaseName + "_missing",
            ConnectTimeout = 2
        }.ConnectionString;
        var factory = new IsolatedFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(text).Options);
        var result = await new UserRepository(factory).GetByUsernameAsync("test-user");
        Assert.Equal(RepositoryStatus.InfrastructureError, result.Status);
        Assert.Null(result.Value);
    }

    private sealed class IsolatedFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [SqlFact]
    public async Task InvalidMetadataAndNonUtcDatesAreRejected()
    {
        await using var scope = await fixture.BeginAsync();
        var user = NewUser();
        await new UserRepository(scope).AddAsync(user);
        var files = new FileRepository(scope);
        foreach (var path in new[] { "../outside.txt", @"C:\outside.txt", "folder/../note.txt", @"folder\note.txt", "/outside.txt", "NUL.txt" })
        {
            var file = NewFile(user.Id);
            file.RelativePath = path;
            Assert.Equal(RepositoryStatus.Validation, (await files.AddAsync(file)).Status);
        }
        var negative = NewFile(user.Id);
        negative.SizeBytes = -1;
        Assert.Equal(RepositoryStatus.Validation, (await files.AddAsync(negative)).Status);
        var localTime = NewFile(user.Id);
        localTime.CreatedAtUtc = DateTime.SpecifyKind(localTime.CreatedAtUtc, DateTimeKind.Unspecified);
        Assert.Equal(RepositoryStatus.Validation, (await files.AddAsync(localTime)).Status);
        Assert.Empty((await files.ListAsync(user.Id)).Value!);
    }
}
