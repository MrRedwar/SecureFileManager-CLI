using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Security;
namespace SecureFileManager.Cli;
public sealed record AuditListItem(string OccurredAt, string Actor, string Action, string Target, string Result, string Description = "")
{
    public string ResultCode => Result switch { "Успех" => "Success", "Отказано" => "Denied", "Ошибка" => "Failed", _ => "Neutral" };
    public static AuditListItem From(Operation o) => new(o.OccurredAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
        o.ActorNameSnapshot is { Length: <= 50 } name && !name.Any(char.IsControl) ? name : "—",
        o.OperationType.ToString(), (o.ExternalDocumentId is not null || Core.Files.ExternalOperationRules.AuditCode(o.Details)) && ExternalPathSyntax.IsValid(o.ExternalPathSnapshot) ? o.ExternalPathSnapshot! :
            RelativePathSyntax.IsValid(o.TargetPathSnapshot) ? o.TargetPathSnapshot! : "—",
        o.Result switch { OperationResult.Success => "Успех", OperationResult.Denied => "Отказано", _ => "Ошибка" }, Describe(o));
    private static string Describe(Operation o) => o.Details switch
    {
        "LOGIN_LOCKED" => "Вход отклонён: временная блокировка.",
        "PERSONAL_STORAGE_PREPARED" => "Регистрация: личное хранилище подготовлено.",
        "METADATA_SYNCHRONIZED" => "Метаданные согласованы по явной команде.",
        "RECOVERY_FINALIZED" => "Завершена подтверждённая операция.",
        "RECOVERY_ROLLED_BACK" => "Отменена незавершённая операция.",
        "STORAGE_DIAGNOSED" => "Неразрушающая диагностика завершена.",
        "STORAGE_DIAGNOSIS_PARTIAL" => "Диагностика неполная; требуется проверка.",
        "COMPUTER_DIRECTORY_LISTED" => "Просмотр общего каталога компьютера. Содержимое файлов не читалось.",
        "EXTERNAL_READ" => "Внешний документ открыт в редакторе.",
        "EXTERNAL_SAVED" => "Внешний документ сохранён, метаданные и аудит подтверждены.",
        "EXTERNAL_VALIDATED" => "Выполнена проверка внешнего JSON/XML.",
        "EXTERNAL_RECOVERED" => "Проверено восстановление внешнего сохранения.",
        "EXTERNAL_OP_CreateFile" => "Создан внешний файл.",
        "EXTERNAL_OP_CreateDirectory" => "Создана внешняя папка.",
        "EXTERNAL_OP_Copy" => "Создана копия внешнего объекта.",
        "EXTERNAL_OP_Move" => "Внешний объект перемещён или переименован.",
        "EXTERNAL_OP_Delete" => "Внешний объект перемещён в корзину приложения.",
        "EXTERNAL_OP_Restore" => "Внешний объект восстановлен из корзины приложения.",
        "EXTERNAL_OP_CreateZip" => "Создан внешний ZIP-архив.",
        "EXTERNAL_OP_ExtractZip" => "Внешний ZIP извлечён в новую папку.",
        "EXTERNAL_OP_PREVIEW" => "Проверено содержимое внешнего ZIP; фактическая распаковка ещё не выполнена.",
        "EXTERNAL_OP_SOURCE" => "Источник внешней файловой операции.",
        "EXTERNAL_OP_RECOVERED" => "Проверено восстановление внешней файловой операции.",
        _ => o.Result == OperationResult.Denied ? "Действие отклонено политикой доступа или безопасности." :
            o.Result == OperationResult.Failed ? "Действие не завершено. Содержимое документа и системные пути скрыты." :
            "Действие выполнено; обязательная запись сохранена."
    };
}
