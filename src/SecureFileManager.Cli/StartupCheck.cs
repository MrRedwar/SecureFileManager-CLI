using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SecureFileManager.Core.Entities;
using SecureFileManager.Data;
using SecureFileManager.Infrastructure.Windows.FileSystem;

namespace SecureFileManager.Cli;

public sealed record CheckResult(bool Success, string Message);
public sealed record ConfiguredStorageRoot(string Path);

// Read-only preflight: no provisioner, audit probe, workspace, repair, or migration calls.
public sealed class StartupCheck(IDbContextFactory<AppDbContext> factory, PersonalStorage paths, ConfiguredStorageRoot location)
{
    public async Task<CheckResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.Database.OpenConnectionAsync(ct);
            var expected = db.Database.GetMigrations().Order().ToArray();
            var actual = (await db.Database.GetAppliedMigrationsAsync(ct)).Order().ToArray();
            if (!expected.SequenceEqual(actual)) return Fail("История миграций отличается от перенесённой версии. Изменение БД запрещено.");
            var model = db.GetService<IDesignTimeModel>().Model;
            foreach (var entity in model.GetEntityTypes())
            {
                var table = entity.GetTableName()!;
                var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
                foreach (var property in entity.GetProperties())
                {
                    // All identifiers originate in the EF model, never in console input.
                    var column = property.GetColumnName(store)!;
                    var type = property.GetColumnType()!;
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT LOWER(TYPE_NAME(c.user_type_id)) + CASE WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar','nchar') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' ELSE CONVERT(varchar(10),c.max_length/2) END + ')' WHEN TYPE_NAME(c.user_type_id) IN ('varchar','char','varbinary','binary') THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' ELSE CONVERT(varchar(10),c.max_length) END + ')' ELSE '' END, c.is_nullable FROM sys.columns c WHERE c.object_id=OBJECT_ID(@table) AND c.name=@column";
                    Add(command, "@table", "dbo." + table); Add(command, "@column", column);
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct) || !string.Equals(reader.GetString(0).Replace("timestamp", "rowversion"), type, StringComparison.OrdinalIgnoreCase) || reader.GetBoolean(1) != property.IsNullable)
                        return Fail($"Столбец {table}.{column} несовместим с AppDbContext.");
                }
                foreach (var index in entity.GetIndexes())
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT is_unique, is_disabled, filter_definition FROM sys.indexes WHERE object_id=OBJECT_ID(@table) AND name=@name";
                    Add(command, "@table", "dbo." + table); Add(command, "@name", index.GetDatabaseName()!);
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct) || reader.GetBoolean(0) != index.IsUnique || reader.GetBoolean(1) ||
                        Normalize(reader.IsDBNull(2) ? null : reader.GetString(2)) != Normalize(index.GetFilter())) return Fail("Индексы схемы несовместимы.");
                }
                foreach (var index in entity.GetIndexes())
                    if (!await IndexColumns(db, table, index.GetDatabaseName()!, index.Properties.Select(x => x.GetColumnName(store)!).ToArray(), false, ct))
                        return Fail("Состав индексов схемы несовместим.");
                if (entity.FindPrimaryKey() is { } primary && !await IndexColumns(db, table, primary.GetName()!, primary.Properties.Select(x => x.GetColumnName(store)!).ToArray(), true, ct))
                    return Fail("Первичный ключ схемы несовместим.");
                foreach (var constraint in entity.GetCheckConstraints())
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT definition FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(@table) AND name=@name AND is_disabled=0 AND is_not_trusted=0";
                    Add(command, "@table", "dbo." + table); Add(command, "@name", constraint.Name!);
                    if (await command.ExecuteScalarAsync(ct) is not string definition || Normalize(definition) != Normalize(constraint.Sql))
                        return Fail($"Ограничение {constraint.Name} несовместимо.");
                }
                foreach (var key in entity.GetForeignKeys())
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(@table) AND name=@name AND is_disabled=0 AND is_not_trusted=0 AND delete_referential_action=0";
                    Add(command, "@table", "dbo." + table); Add(command, "@name", key.GetConstraintName()!);
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1) return Fail("Внешние ключи схемы несовместимы.");
                    command.Parameters.Clear();
                    command.CommandText = "SELECT pc.name, OBJECT_NAME(fkc.referenced_object_id), rc.name FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id=fkc.constraint_object_id JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id WHERE fk.parent_object_id=OBJECT_ID(@table) AND fk.name=@name ORDER BY fkc.constraint_column_id";
                    Add(command, "@table", "dbo." + table); Add(command, "@name", key.GetConstraintName()!);
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    var principalStore = StoreObjectIdentifier.Table(key.PrincipalEntityType.GetTableName()!, key.PrincipalEntityType.GetSchema());
                    for (var i = 0; i < key.Properties.Count; i++)
                        if (!await reader.ReadAsync(ct) || reader.GetString(0) != key.Properties[i].GetColumnName(store) || reader.GetString(1) != key.PrincipalEntityType.GetTableName() || reader.GetString(2) != key.PrincipalKey.Properties[i].GetColumnName(principalStore))
                            return Fail("Связи схемы несовместимы.");
                    if (await reader.ReadAsync(ct)) return Fail("Связи схемы несовместимы.");
                }
            }
            var users = await db.Users.AsNoTracking().Select(x => x.Id).Take(10001).ToListAsync(ct);
            if (users.Count > 10000) return Fail("Проверка ограничена 10000 пользователями; нужна отдельная проверка установки.");
            var root = Path.GetFullPath(location.Path);
            if (!Directory.Exists(Path.Combine(root, "Users"))) return Fail("StorageRoot/Users отсутствует. Укажите существующее хранилище WPF.");
            foreach (var user in users)
                if (!paths.Resolve(user, "").Succeeded) return Fail("StorageRoot не содержит доступные каталоги пользователей этой БД. Автовосстановление запрещено.");
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "Users")))
                if (!Guid.TryParse(Path.GetFileName(directory), out var id) || !users.Contains(id)) return Fail("В StorageRoot найдены каталоги, не соответствующие пользователям БД.");
            var rows = await db.Files.AsNoTracking().Take(10001).ToListAsync(ct);
            if (rows.Count > 10000) return Fail("Проверка ограничена 10000 файлами; нужна отдельная проверка установки.");
            var mismatches = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var resolved = paths.Resolve(row.OwnerId, row.RelativePath);
                if (!resolved.Succeeded || (row.Kind == FileKind.Directory ? !Directory.Exists(resolved.PhysicalPath) : !File.Exists(resolved.PhysicalPath)))
                { mismatches++; continue; }
                if (row.Kind == FileKind.File && new FileInfo(resolved.PhysicalPath!).Length != row.SizeBytes)
                    mismatches++;
            }
            return new(true, "Подключение, схема и соответствие каталогов пользователей проверены без записи. " +
                (mismatches > 0 ? $"ВНИМАНИЕ: расхождений объектов Files — {mismatches}. Проверьте StorageRoot; после входа доступны явная диагностика/восстановление. Автоматических исправлений нет. " : "") +
                "Одновременная работа с WPF не сертифицирована: завершите сеанс WPF перед работой CLI.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail("Проверка отменена."); }
        catch { return Fail("Проверка SQL/схемы/StorageRoot не пройдена. Проверьте доступ и конфигурацию; данные не исправлялись."); }
    }
    private static string Normalize(string? value)
    {
        var text = new string((value ?? "").Where(c => !char.IsWhiteSpace(c) && c is not '(' and not ')' and not '[' and not ']').Select(char.ToUpperInvariant).ToArray());
        // SQL Server expands BETWEEN and IN when persisting CHECK definitions.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"([A-Z][A-Z0-9_]*)BETWEEN(\d+)AND(\d+)", m => m.Groups[1].Value + ">=" + m.Groups[2].Value + "AND" + m.Groups[1].Value + "<=" + m.Groups[3].Value);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"([A-Z][A-Z0-9_]*)IN('[^']*'(?:,'[^']*')*)", m => string.Join("OR", m.Groups[2].Value.Split(',').Reverse().Select(x => m.Groups[1].Value + "=" + x)));
        return text;
    }
    private static CheckResult Fail(string message) => new(false, message);
    private static async Task<bool> IndexColumns(AppDbContext db, string table, string name, string[] expected, bool primary, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT c.name, i.is_primary_key FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID(@table) AND i.name=@name AND ic.key_ordinal>0 ORDER BY ic.key_ordinal";
        Add(command, "@table", "dbo." + table); Add(command, "@name", name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        foreach (var column in expected)
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != column || reader.GetBoolean(1) != primary) return false;
        return !await reader.ReadAsync(ct);
    }
    private static void Add(System.Data.Common.DbCommand command, string name, string value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}
