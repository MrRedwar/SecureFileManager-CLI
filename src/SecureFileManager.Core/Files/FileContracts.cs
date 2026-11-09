using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;

namespace SecureFileManager.Core.Files;

public enum FileStatus { Success, Denied, Invalid, NotFound, Conflict, Unavailable, DatabaseUnavailable, RecoveryRequired, Unsupported, LimitExceeded, InvalidDocument,
    ArchiveInvalid, ArchiveUnsupported, ArchiveEncrypted, ArchiveUnsafe, ArchiveLimit, Cancelled }
public sealed record FileResult<T>(FileStatus Status, T? Value = default, DocumentValidation? Validation = null)
{
    public bool Succeeded => Status == FileStatus.Success;
    public string Message => Validation?.Description ?? (Status switch
    {
        FileStatus.Success => "",
        FileStatus.Denied => "Доступ запрещён. Проверьте действующий сеанс и владельца объекта.",
        FileStatus.Invalid => "Недопустимое имя или путь.",
        FileStatus.NotFound => "Объект не найден. Обновите список.",
        FileStatus.Conflict => "Объект уже существует, занят или изменён другим процессом. Обновите список.",
        FileStatus.DatabaseUnavailable => "База данных или обязательный аудит недоступны. Операция не подтверждена.",
        FileStatus.RecoveryRequired => "Есть незавершённая операция. Нажмите «Восстановить операции». Данные сохранены для проверки.",
        FileStatus.Unsupported => "Поддерживаются TXT, JSON и XML в строгой UTF-8. Проверьте формат и кодировку файла.",
        FileStatus.LimitExceeded => "Превышен лимит: текст — 10 МиБ, импорт — 100 МиБ, список — 10000 объектов.",
        FileStatus.InvalidDocument => "Документ некорректен. Исправьте ошибки перед сохранением.",
        FileStatus.ArchiveInvalid => "ZIP повреждён: проверьте структуру, размеры и целостность данных.",
        FileStatus.ArchiveUnsupported => "Этот формат ZIP не поддерживается. Разрешены обычные ZIP (Store/Deflate), без ZIP64 и многотомных архивов.",
        FileStatus.ArchiveEncrypted => "Зашифрованный ZIP не поддерживается. Используйте незашифрованную копию.",
        FileStatus.ArchiveUnsafe => "ZIP отклонён: опасный путь, ссылка, дубликат или конфликт структуры.",
        FileStatus.ArchiveLimit => "Превышен лимит ZIP: 250 записей, 25 МиБ на файл, 100 МиБ всего, коэффициент 100:1.",
        FileStatus.Cancelled => "Операция отменена. Успешное завершение не подтверждено.",
        _ => "Хранилище недоступно. Проверьте накопитель, свободное место и права доступа."
    });
}
public sealed class FileCommandException(FileStatus status, DocumentValidation? validation = null, DocumentFormat? format = null, Exception? inner = null) : Exception("File operation rejected.", inner)
{
    public FileStatus Status { get; } = status;
    public DocumentValidation? Validation { get; } = validation;
    public DocumentFormat? Format { get; } = format;
}
public sealed class FileStoreException(bool uncertain) : Exception("File transaction unavailable.")
{ public bool CommitUncertain { get; } = uncertain; }
public enum EntryState { Managed, Untracked, Missing, Changed, Unsafe }
public sealed record PhysicalEntry(string RelativePath, string Name, FileKind Kind, long SizeBytes, DateTime CreatedUtc, DateTime ModifiedUtc, bool IsSafe = true);
public sealed record FileEntry(Guid? Id, string RelativePath, string Name, FileKind Kind, long SizeBytes, DateTime ModifiedUtc, EntryState State);
public sealed record TextDocument(Guid Id, string Name, string Content, string Version)
{
    public DocumentValidation? Validation { get; init; }
    public override string ToString() => "[Text document content omitted]";
}
public sealed class ImportSelection(string sourcePath, string name)
{
    public string SourcePath { get; } = sourcePath;
    public string Name { get; } = name;
    public override string ToString() => "[Explicit file selection]";
}
public sealed record DriveSnapshot(string Name, string Label, string Type, string Format, bool IsReady, long Total, long Free)
{
    public long Used => Math.Max(0, Total - Free);
    public double Percent => Total > 0 ? 100d * Used / Total : 0;
}
public interface IDriveService { Task<FileResult<IReadOnlyList<DriveSnapshot>>> GetAsync(CancellationToken ct = default); }
public interface IFileService : ITextDocumentService
{
    event EventHandler<FileChangeNotification>? Changed;
    Task WaitForIdleAsync();
    Task<FileResult<IReadOnlyList<FileEntry>>> ListAsync(string directory, CancellationToken ct = default);
    Task<FileResult<bool>> CreateAsync(string directory, string name, bool folder, CancellationToken ct = default);
    Task<FileResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<FileResult<bool>> SynchronizeAsync(string relativePath, CancellationToken ct = default);
    Task<FileResult<bool>> ImportAsync(string directory, ImportSelection source, CancellationToken ct = default);
    Task<FileResult<bool>> RecoverAsync(CancellationToken ct = default);
}
public sealed class FileChangeNotification(Guid owner) : EventArgs { public Guid Owner { get; } = owner; }
public interface IFileWork
{
    IFileRepository Files { get; }
    IOperationRepository Audit { get; }
    Task<bool> HasSuccessAsync(Guid correlationId, CancellationToken ct);
}
public interface IFileTransactionStore
{
    Task<T> ExecuteAsync<T>(Guid owner, Func<IFileWork, Task<T>> work, CancellationToken ct);
    Task<bool> HasSuccessAsync(Guid owner, Guid correlationId, CancellationToken ct);
}
public enum ChangeKind { Create, Directory, Save, Delete, Import, Archive, Extract }
public sealed record FileJournal(Guid Id, ChangeKind Kind, string RelativePath, bool IsDirectory,
    string? BeforeVersion, string? AfterVersion)
{ public IReadOnlyList<PreparedEntry>? Tree { get; init; } }
public sealed record PhysicalText(string Content, string Version);
public interface IFileWorkspace : IDisposable
{
    IReadOnlyList<PhysicalEntry> List(string directory);
    PhysicalEntry Inspect(string path);
    PhysicalText Read(string path);
    BinaryDocument OpenBinary(string path, long limit);
    IReadOnlyList<FileJournal> Pending();
    FileJournal Stage(Guid id, ChangeKind kind, string path, string? text = null, string? expected = null, ImportSelection? source = null);
    FileJournal StageGenerated(Guid id, string path, bool directory, Action<IFilePreparation> prepare, CancellationToken ct);
    void Publish(FileJournal journal);
    void Settle(FileJournal journal, bool committed);
}
public interface IPhysicalFileStorage { IFileWorkspace Open(Guid owner); }
public static class FileRules
{
    public const int TextLimit = 10 * 1024 * 1024;
    public const int ImportLimit = 100 * 1024 * 1024;
    public const int ListLimit = 10000;
    public static void Path(string? path, bool allowRoot = false)
    {
        if (path == "" && allowRoot) return;
        if (!Security.RelativePathSyntax.IsValid(path) || path!.Split('/').Any(x => x.StartsWith(".sfm-", StringComparison.OrdinalIgnoreCase)))
            throw new FileCommandException(FileStatus.Invalid);
    }
    public static string Child(string directory, string name)
    {
        Path(directory, true);
        if (!Security.RelativePathSyntax.IsNameValid(name)) throw new FileCommandException(FileStatus.Invalid);
        var path = directory == "" ? name : directory + "/" + name;
        if (name.Contains('.') && name[(name.LastIndexOf('.') + 1)..].Length > 31)
            throw new FileCommandException(FileStatus.Invalid);
        Path(path); return path;
    }
    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static void Text(string name) => _ = DocumentValidator.Format(name);
}
public static class FileDisplay
{
    public static string Bytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = Math.Max(0, value); var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.##", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
