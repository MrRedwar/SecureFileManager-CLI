using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SecureFileManager.Core.Entities;

namespace SecureFileManager.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<ExternalDocument> ExternalDocuments => Set<ExternalDocument>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Stable comparison independent of the server/database default collation.
        const string collation = "Latin1_General_100_CI_AS";
        var external = model.Entity<ExternalDocument>();
        external.ToTable("ExternalDocuments", t => t.HasCheckConstraint("CK_ExternalDocuments_Size", "[SizeBytes] >= 0"));
        external.HasKey(x => x.Id);
        external.Property(x => x.Path).HasMaxLength(4000).IsRequired();
        external.Property(x => x.PathKey).HasMaxLength(64).IsUnicode(false).IsRequired();
        external.HasIndex(x => x.PathKey).IsUnique();
        external.Property(x => x.Version).HasMaxLength(256).IsRequired();
        external.Property(x => x.RowVersion).IsRowVersion();
        var users = model.Entity<User>();
        users.ToTable("Users", t =>
        {
            t.HasCheckConstraint("CK_Users_FailedLoginCount", "[FailedLoginCount] >= 0");
            t.HasCheckConstraint("CK_Users_Username", "LEN(LTRIM(RTRIM([Username]))) BETWEEN 3 AND 50 AND DATALENGTH([Username]) = DATALENGTH(LTRIM(RTRIM([Username])))");
            t.HasCheckConstraint("CK_Users_PasswordHash", "LEN([PasswordHash]) > 0");
        });
        users.HasKey(x => x.Id);
        users.Property(x => x.Username).HasMaxLength(50).UseCollation(collation).IsRequired();
        users.Property(x => x.PasswordHash).HasMaxLength(1024).IsRequired();
        users.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
        users.HasIndex(x => x.Username).IsUnique();

        var files = model.Entity<FileRecord>();
        files.ToTable("Files", t =>
        {
            t.HasCheckConstraint("CK_Files_SizeBytes", "[SizeBytes] >= 0");
            t.HasCheckConstraint("CK_Files_Kind", "[Kind] IN ('File', 'Directory')");
            t.HasCheckConstraint("CK_Files_Name", "LEN([Name]) > 0");
            t.HasCheckConstraint("CK_Files_RelativePath", "LEN([RelativePath]) > 0");
            t.HasCheckConstraint("CK_Files_SoftDelete", "([IsDeleted] = 0 AND [DeletedAtUtc] IS NULL) OR ([IsDeleted] = 1 AND [DeletedAtUtc] IS NOT NULL)");
            t.HasCheckConstraint("CK_Files_Dates", "[ModifiedAtUtc] >= [CreatedAtUtc] AND ([DeletedAtUtc] IS NULL OR [DeletedAtUtc] >= [CreatedAtUtc])");
        });
        files.HasKey(x => x.Id);
        files.Property(x => x.Name).HasMaxLength(255).IsRequired();
        files.Property(x => x.RelativePath).HasMaxLength(400).UseCollation(collation).IsRequired();
        files.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        files.Property(x => x.Extension).HasMaxLength(32);
        files.Property(x => x.RowVersion).IsRowVersion();
        files.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.NoAction);
        files.HasIndex(x => x.OwnerId);
        files.HasIndex(x => new { x.OwnerId, x.RelativePath }).IsUnique().HasFilter("[IsDeleted] = 0");
        files.HasQueryFilter(x => !x.IsDeleted);

        var operations = model.Entity<Operation>();
        operations.ToTable("Operations", t =>
        {
            t.HasCheckConstraint("CK_Operations_Result", "[Result] IN ('Success', 'Denied', 'Failed')");
            var types = string.Join(", ", Enum.GetNames<OperationType>().Select(x => $"'{x}'"));
            t.HasCheckConstraint("CK_Operations_Type", $"[OperationType] IN ({types})");
        });
        operations.HasKey(x => x.Id);
        operations.Property(x => x.ActorNameSnapshot).HasMaxLength(50);
        operations.Property(x => x.TargetPathSnapshot).HasMaxLength(400);
        operations.Property(x => x.ExternalPathSnapshot).HasMaxLength(4000);
        operations.HasOne<ExternalDocument>().WithMany().HasForeignKey(x => x.ExternalDocumentId).OnDelete(DeleteBehavior.NoAction);
        operations.Property(x => x.Details).HasMaxLength(512);
        operations.Property(x => x.OperationType).HasConversion<string>().HasMaxLength(32);
        operations.Property(x => x.Result).HasConversion<string>().HasMaxLength(16);
        operations.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        operations.HasOne<FileRecord>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.NoAction);
        operations.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
        operations.HasIndex(x => new { x.OperationType, x.Result });
        operations.HasIndex(x => x.CorrelationId);

        // SQL datetime2 has no timezone; reject non-UTC writes and restore Kind on reads.
        var utc = new ValueConverter<DateTime, DateTime>(x => x, x => DateTime.SpecifyKind(x, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                property.SetValueConverter(utc);
    }

    private void ValidateChanges()
    {
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is Operation && entry.State != EntityState.Added)
                throw new PersistenceValidationException(); // audit is append-only through this context
            if (entry.State == EntityState.Deleted)
            {
                if (entry.Entity is not FileRecord file) throw new PersistenceValidationException();
                entry.State = EntityState.Unchanged;
                file.IsDeleted = true;
                file.DeletedAtUtc = DateTime.UtcNow;
                file.ModifiedAtUtc = file.DeletedAtUtc.Value;
                entry.Property(nameof(FileRecord.IsDeleted)).IsModified = true;
                entry.Property(nameof(FileRecord.DeletedAtUtc)).IsModified = true;
                entry.Property(nameof(FileRecord.ModifiedAtUtc)).IsModified = true;
            }
            foreach (var property in entry.Properties)
                if (property.CurrentValue is DateTime date && date.Kind != DateTimeKind.Utc)
                    throw new PersistenceValidationException();
            switch (entry.Entity)
            {
                case ExternalDocument d when d.Id == Guid.Empty || !Core.Security.ExternalPathSyntax.IsValid(d.Path) ||
                    d.PathKey.Length != 64 || d.Version.Length is < 1 or > 256 || d.SizeBytes < 0:
                    throw new PersistenceValidationException();
                case User u when u.Id == Guid.Empty || string.IsNullOrWhiteSpace(u.Username) ||
                    u.Username.Length is < 3 or > 50 || u.Username != u.Username.Trim() ||
                    string.IsNullOrWhiteSpace(u.PasswordHash) || u.FailedLoginCount < 0:
                    throw new PersistenceValidationException();
                case FileRecord f when f.Id == Guid.Empty || f.OwnerId == Guid.Empty ||
                    !MetadataPath.IsValid(f.RelativePath) || !MetadataPath.IsNameValid(f.Name) ||
                    f.RelativePath.Split('/')[^1] != f.Name || f.SizeBytes < 0 || !Enum.IsDefined(f.Kind) ||
                    f.ModifiedAtUtc < f.CreatedAtUtc || f.IsDeleted != f.DeletedAtUtc.HasValue:
                    throw new PersistenceValidationException();
                case Operation o when o.Id == Guid.Empty || o.CorrelationId == Guid.Empty ||
                    (o.FileId is not null && o.ExternalDocumentId is not null) ||
                    (o.ExternalPathSnapshot is not null && ((!Core.Files.ExternalOperationRules.AuditCode(o.Details) && o.ExternalDocumentId is null) || !Core.Security.ExternalPathSyntax.IsValid(o.ExternalPathSnapshot))) ||
                    !Enum.IsDefined(o.OperationType) || !Enum.IsDefined(o.Result) ||
                    (o.TargetPathSnapshot is not null && !MetadataPath.IsValid(o.TargetPathSnapshot)):
                    throw new PersistenceValidationException();
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

internal sealed class PersistenceValidationException : Exception;

// Metadata syntax only: physical paths/reparse-point checks belong to stage 04/05 services.
internal static class MetadataPath
{
    public static bool IsValid(string? path) => SecureFileManager.Core.Security.RelativePathSyntax.IsValid(path);
    public static bool IsNameValid(string? name) => SecureFileManager.Core.Security.RelativePathSyntax.IsNameValid(name);
}
