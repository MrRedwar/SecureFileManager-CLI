using Microsoft.Extensions.DependencyInjection;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Journal;
using SecureFileManager.Infrastructure.Windows.FileSystem;

namespace SecureFileManager.Cli;

public sealed class ConsoleApplication(IServiceProvider services, ConsoleUi? terminal = null)
{
    private readonly ConsoleUi ui = terminal ?? new();
    private CancellationTokenSource? pending;
    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
    private IUserSession Session => Get<IUserSession>();
    private IFileService Files => Get<IFileService>();
    private IExternalFileOperationService External => Get<IExternalFileOperationService>();

    public async Task RunAsync()
    {
        void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; pending?.Cancel(); }
        Console.CancelKeyPress += Cancel;
        try
        {
            var exit = false;
            while (!exit)
            {
                try
                {
                    ui.Line("\n========================================\n         SECURE FILE MANAGER CLI\n========================================");
                    if (Session.Current is null)
                    {
                        ui.Line("1. Регистрация\n2. Авторизация\n0. Выход");
                        switch (ui.Read("Выбор"))
                        {
                            case "0": exit = true; break;
                            case "1": await Register(); break;
                            case "2": await Login(); break;
                            default: ui.Line("Неизвестный пункт меню."); break;
                        }
                    }
                    else
                    {
                        ui.Line($"Пользователь: {Session.Current.Username}\n1. Диски и каталоги\n2. Мои файлы\n3. Работа с документами\n4. ZIP-архивы\n5. Журнал операций\n6. Настройки\n7. Выход из аккаунта\n0. Завершение программы");
                        switch (ui.Read("Выбор"))
                        {
                            case "0": exit = true; break;
                            case "1": await Browse(); break;
                            case "2": await Personal(); break;
                            case "3": await Documents(); break;
                            case "4": await Archives(); break;
                            case "5": await Journal(); break;
                            case "6": await Settings(); break;
                            case "7": await Logout(); break;
                            default: ui.Line("Неизвестный пункт меню."); break;
                        }
                    }
                }
                catch (EndOfStreamException) { exit = true; }
                catch (OperationCanceledException) { ui.Line("Ввод или операция отменены."); }
                catch (ArgumentException) { ui.Line("Некорректный ввод. Повторите выбор."); }
                catch (InvalidOperationException e) when (e.Message.StartsWith("Для пароля")) { ui.Line(e.Message); }
                catch { ui.Line("Операция не подтверждена. Проверьте подключение, права и состояние хранилища."); }
            }
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
            if (Session.Current is not null) await Logout();
        }
    }
    private async Task Register()
    {
        var login = ui.Read("Логин");
        var password = ui.Password("Пароль");
        var confirmation = ui.Password("Повторите пароль");
        try { var r = await Get<IRegistrationService>().RegisterAsync(login, password, confirmation); ui.Line(r.Message); }
        finally { password = confirmation = ""; }
    }
    private async Task Login()
    {
        var login = ui.Read("Логин"); var password = ui.Password("Пароль");
        try
        {
            var result = await Get<IAuthenticationService>().LoginAsync(login, password);
            ui.Line(result.Message);
            // Existing users must never receive a newly provisioned duplicate workspace on login.
            if (result.Succeeded)
            {
                var check = await Get<IUserStorageService>().CheckPathAsync(Session.Current!.Id, "");
                if (!check.Succeeded) { ui.Line("Существующее личное хранилище недоступно. Сеанс завершается."); await Logout(); }
            }
        }
        finally { password = ""; }
    }
    private async Task Logout()
    {
        await Files.WaitForIdleAsync();
        var result = await Get<IAuthenticationService>().LogoutAsync();
        ui.Line(result.Succeeded ? "Сеанс завершён." : result.Message);
    }
    private async Task<FileResult<T>> Run<T>(Func<CancellationToken, Task<FileResult<T>>> operation)
    {
        using var source = new CancellationTokenSource(); pending = source;
        ui.Line("Выполнение… Ctrl+C — запрос отмены, если операция поддерживает отмену.");
        try { var r = await operation(source.Token); if (!r.Succeeded) ui.Line($"{r.Status}: {r.Message}"); return r; }
        finally { pending = null; }
    }
    private async Task Done(Func<CancellationToken, Task<FileResult<bool>>> operation)
    { if ((await Run(operation)).Succeeded) ui.Line("Операция выполнена."); }

    private async Task Personal()
    {
        var path = ""; var search = ""; var sort = "1";
        while (true)
        {
            var listing = await Run(ct => Files.ListAsync(path, ct));
            if (!listing.Succeeded)
            {
                if (listing.Status == FileStatus.RecoveryRequired && ui.Confirm("Список заблокирован незавершённой операцией. Выполнить штатное восстановление?"))
                {
                    if ((await Run(ct => Files.RecoverAsync(ct))).Succeeded) { path = ""; continue; }
                }
                return;
            }
            IEnumerable<FileEntry> filtered = listing.Value!.Where(x => x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            var rows = (sort switch { "2" => filtered.OrderBy(x => x.SizeBytes), "3" => filtered.OrderByDescending(x => x.ModifiedUtc), _ => filtered.OrderByDescending(x => x.Kind == FileKind.Directory).ThenBy(x => x.Name) }).ToArray();
            ui.Line("\nМои файлы: /" + path);
            ui.Table(rows.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.Kind.ToString(), FileDisplay.Bytes(x.SizeBytes), x.ModifiedUtc.ToLocalTime().ToString("g"), x.State.ToString() }), "№", "Имя", "Тип", "Размер", "Изменён", "Состояние");
            ui.Line("1. Открыть  2. Вверх  3. Создать документ  4. Создать папку\n5. Импорт  6. Удалить  7. Сверить метаданные  8. Восстановить операции\n9. ZIP  10. Поиск  11. Сортировка  12. Свойства  13. Обновить  0. Назад");
            try
            {
                switch (ui.Read("Выбор"))
                {
                    case "0": return;
                    case "1":
                        var entry = rows[ui.Index(rows.Length)];
                        if (entry.Kind == FileKind.Directory) path = entry.RelativePath;
                        else if (entry.Id is { } id) await OpenPersonal(id);
                        else ui.Line("Объект не учтён. Явная сверка доступна в пункте 7.");
                        break;
                    case "2": path = FileRules.Parent(path); break;
                    case "3": var documentName = ui.Read("Имя TXT/JSON/XML"); await Done(ct => Files.CreateAsync(path, documentName, false, ct)); break;
                    case "4": var folder = ui.Read("Имя папки"); await Done(ct => Files.CreateAsync(path, folder, true, ct)); break;
                    case "5": var source = ui.Read("Полный путь исходного файла"); var name = ui.Read("Имя копии"); await Done(ct => Files.ImportAsync(path, new(source, name), ct)); break;
                    case "6": var deleted = rows[ui.Index(rows.Length)]; if (deleted.Id is { } deleteId && ui.Confirm("Удалить " + deleted.Name + "? Личное удаление без корзины")) await Done(ct => Files.DeleteAsync(deleteId, ct)); break;
                    case "7": var sync = rows[ui.Index(rows.Length)]; if (ui.Confirm("Сверить метаданные выбранного объекта?")) await Done(ct => Files.SynchronizeAsync(sync.RelativePath, ct)); break;
                    case "8": if (ui.Confirm("Проверить и восстановить незавершённые операции?")) await Done(ct => Files.RecoverAsync(ct)); break;
                    case "9": await PersonalZip(path, rows); break;
                    case "10": search = ui.Read("Часть имени (пусто — все)"); break;
                    case "11": sort = ui.Read("1 — имя; 2 — размер; 3 — дата"); break;
                    case "12": var item = rows[ui.Index(rows.Length)]; ui.Line($"Путь: /{item.RelativePath}\nID: {item.Id}\nТип: {item.Kind}\nРазмер: {item.SizeBytes} Б\nИзменён UTC: {item.ModifiedUtc:O}\nСостояние: {item.State}"); break;
                    case "13": break;
                    default: ui.Line("Неизвестный пункт меню."); break;
                }
            }
            catch (OperationCanceledException) { ui.Line("Отменено."); }
            catch (ArgumentException) { ui.Line("Некорректный ввод."); }
        }
    }
    private async Task OpenPersonal(Guid id)
    { var result = await Run(ct => Files.ReadAsync(id, ct)); if (result.Succeeded) await Editor(Files, result.Value!); }

    private async Task Browse()
    {
        var path = ""; var search = ""; var sort = "1";
        while (true)
        {
            var result = await Run(ct => Get<IComputerBrowseService>().ListAsync(path, ct));
            if (!result.Succeeded) { path = ""; if (!ui.Confirm("Вернуться к списку дисков?")) return; continue; }
            var directory = result.Value!;
            var filtered = directory.Entries.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            var rows = (sort switch { "2" => filtered.OrderBy(x => x.SizeBytes), "3" => filtered.OrderByDescending(x => x.ModifiedUtc), _ => filtered.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name) }).ToArray();
            ui.Line("\nКомпьютер: " + (path == "" ? "Диски" : directory.Path));
            if (directory.IsTruncated) ui.Line("Список ограничен 10000 объектами.");
            if (directory.Path == "")
            {
                var capacity = await Run(ct => Get<IDriveService>().GetAsync(ct));
                var drives = capacity.Value?.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
                ui.Table(rows.Select((x, i) =>
                {
                    var drive = drives?.GetValueOrDefault(x.Path);
                    return new[] { (i + 1).ToString(), x.Name, drive?.Label ?? "—", drive?.Format ?? "—",
                        drive is { IsReady: true } ? FileDisplay.Bytes(drive.Total) : "—",
                        drive is { IsReady: true } ? FileDisplay.Bytes(drive.Used) : "—",
                        drive is { IsReady: true } ? FileDisplay.Bytes(drive.Free) : "—", x.Note };
                }), "№", "Диск", "Метка", "ФС", "Всего", "Занято", "Свободно", "Примечание");
            }
            else
                ui.Table(rows.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.IsDirectory ? "Папка/диск" : "Файл", x.SizeBytes?.ToString() ?? "—", x.ModifiedUtc?.ToLocalTime().ToString("g") ?? "—", x.Note }), "№", "Имя", "Тип", "Байт", "Изменён", "Примечание");
            ui.Line("1. Открыть  2. Вверх  3. Перейти по пути  4. Свойства дисков\n5. Создать файл  6. Создать папку  7. Копировать  8. Переместить  9. Переименовать\n10. В корзину  11. Восстановить по исходному пути  12. ZIP\n13. Восстановить операции  14. Восстановить документ  15. Свойства объекта\n16. Поиск  17. Сортировка  18. Обновить  0. Назад");
            try
            {
                switch (ui.Read("Выбор"))
                {
                    case "0": return;
                    case "1": var selected = rows[ui.Index(rows.Length)]; if (selected.CanEnter) path = selected.Path; else if (!selected.IsDirectory) await OpenExternal(selected.Path); else ui.Line(selected.Note); break;
                    case "2": path = directory.ParentPath ?? ""; break;
                    case "3": path = ui.Read("Абсолютный путь каталога (пусто — компьютер)"); break;
                    case "4": var drives = await Run(ct => Get<IDriveService>().GetAsync(ct)); if (drives.Succeeded) ui.Table(drives.Value!.Select(x => new[] { x.Name, x.Label, x.Type, x.Format, x.IsReady ? "Да" : "Нет", x.IsReady ? FileDisplay.Bytes(x.Total) : "—", x.IsReady ? FileDisplay.Bytes(x.Used) : "—", x.IsReady ? FileDisplay.Bytes(x.Free) : "—" }), "Диск", "Метка", "Тип", "ФС", "Готов", "Всего", "Занято", "Свободно"); break;
                    case "5": await ExternalCreate(path, false); break;
                    case "6": await ExternalCreate(path, true); break;
                    case "7": await ExternalTransfer(rows[ui.Index(rows.Length)], false, false); break;
                    case "8": await ExternalTransfer(rows[ui.Index(rows.Length)], true, false); break;
                    case "9": await ExternalTransfer(rows[ui.Index(rows.Length)], true, true); break;
                    case "10": var trash = rows[ui.Index(rows.Length)]; if (ui.Confirm("Переместить в корзину приложения: " + trash.Name + "? Место на диске не освобождается")) await Done(ct => External.ExecuteAsync(new(ExternalChange.Delete, trash.Path, "", ""), ct)); break;
                    case "11": var original = ui.Read("Полный исходный путь удалённого объекта (также доступен в журнале)"); if (ui.Confirm("Восстановить объект по проверенной квитанции без перезаписи?")) await Done(ct => External.ExecuteAsync(new(ExternalChange.Restore, original, "", ""), ct)); break;
                    case "12": await ExternalZip(rows[ui.Index(rows.Length)].Path); break;
                    case "13": if (ui.Confirm("Проверить незавершённые внешние операции?")) await Done(ct => External.RecoverAsync(ct)); break;
                    case "14": var recovery = rows[ui.Index(rows.Length)]; if (ui.Confirm("Проверить восстановление документа?")) await Done(ct => Get<IExternalDocumentService>().RecoverAsync(recovery.Path, ct)); break;
                    case "15": var item = rows[ui.Index(rows.Length)]; ui.Line($"Путь: {item.Path}\nКаталог: {item.IsDirectory}\nРазмер: {item.SizeBytes} Б\nИзменён UTC: {item.ModifiedUtc:O}\n{item.Note}"); break;
                    case "16": search = ui.Read("Часть имени (пусто — все)"); break;
                    case "17": sort = ui.Read("1 — имя; 2 — размер; 3 — дата"); break;
                    case "18": break;
                    default: ui.Line("Неизвестный пункт меню."); break;
                }
            }
            catch (OperationCanceledException) { ui.Line("Отменено."); }
            catch (ArgumentException) { ui.Line("Некорректный ввод."); }
        }
    }
    private async Task ExternalCreate(string directory, bool folder)
    { var name = ui.Read("Имя нового " + (folder ? "каталога" : "файла")); await Done(ct => External.ExecuteAsync(new(folder ? ExternalChange.CreateDirectory : ExternalChange.CreateFile, null, directory, name), ct)); }
    private async Task ExternalTransfer(ComputerEntry source, bool move, bool rename)
    {
        var directory = rename ? Path.GetDirectoryName(source.Path)! : ui.Read("Полный путь папки назначения");
        var name = ui.Read("Имя результата");
        await Done(ct => External.ExecuteAsync(new(move ? ExternalChange.Move : ExternalChange.Copy, source.Path, directory, name), ct));
    }
    private async Task OpenExternal(string path)
    { var result = await Run(ct => Get<IExternalDocumentService>().OpenAsync(path, ct)); if (result.Succeeded) await Editor(Get<IExternalDocumentService>(), result.Value!); }
    private async Task Documents()
    {
        ui.Line("TXT/JSON/XML: 1. Личные документы (создание и редактор)  2. Внешний документ по пути  0. Назад");
        switch (ui.Read("Выбор")) { case "1": await Personal(); break; case "2": await OpenExternal(ui.Read("Полный путь TXT/JSON/XML")); break; case "0": break; default: ui.Line("Неизвестный пункт меню."); break; }
    }
    private async Task Editor(ITextDocumentService service, TextDocument document)
    {
        var text = document.Content; var original = text; var version = document.Version;
        async Task<bool> Save()
        {
            var saved = await Run(ct => service.SaveAsync(document.Id, text, version, ct));
            if (!saved.Succeeded) { ui.Line("Буфер сохранён в редакторе. При конфликте перечитайте файл после подтверждения."); return false; }
            original = text; version = "";
            var read = await Run(ct => service.ReadAsync(document.Id, ct));
            if (read.Succeeded && read.Value!.Content == text) version = read.Value.Version;
            else ui.Line("Запись подтверждена; актуальная версия не подтверждена. Перед следующим сохранением перечитайте файл.");
            return true;
        }
        while (true)
        {
            ui.Line($"\nДокумент: {document.Name} · UTF-8 · до 10 МиБ · {(text == original ? "без изменений" : "есть изменения")}\n1. Просмотр  2. Редактировать строку  3. Валидация  4. Сохранить\n5. Перечитать с диска  6. Отменить изменения буфера  7. Полностью заменить текст\n8. Вставить строку  9. Удалить строку  10. Заменить фрагмент  0. Закрыть");
            try
            {
                switch (ui.Read("Выбор"))
                {
                    case "0": if (text == original) return; var decision = ui.Read("Есть изменения: 1 — сохранить и закрыть, 2 — закрыть без сохранения, иначе — продолжить"); if (decision == "2" || decision == "1" && await Save()) return; break;
                    case "1": ui.Line(text); break;
                    case "2":
                        var existingLines = TextEdits.Lines(text);
                        ShowLines(existingLines);
                        var selectedLine = ui.Index(existingLines.Count);
                        text = TextEdits.ChangeLine(text, selectedLine, ui.EditLine("Строка " + (selectedLine + 1), existingLines[selectedLine]));
                        break;
                    case "7":
                        ui.Line("Введите весь новый текст. .end — закончить; /cancel — оставить прежний буфер. Переводы строк при вводе — Windows CRLF.");
                        var lines = new List<string>(); var bytes = 0;
                        while (true) { var line = ui.Read("Текст"); if (line == ".end") break; bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 2; if (bytes > FileRules.TextLimit) throw new ArgumentException(); lines.Add(line); }
                        text = TextEdits.Check(string.Join("\r\n", lines)); break;
                    case "3": var validation = await Run(ct => service.ValidateAsync(document.Id, text, ct)); if (validation.Succeeded) ui.Line(validation.Value!.Description); break;
                    case "4": await Save(); break;
                    case "5":
                        if (text != original && !ui.Confirm("Заменить буфер версией с диска?")) break;
                        var reload = await Run(ct => service.ReloadAsync(document.Id, ct));
                        if (reload.Succeeded) { document = reload.Value!; text = original = document.Content; version = document.Version; ui.Line(document.Validation?.Description ?? "Прочитано."); }
                        break;
                    case "6": if (ui.Confirm("Отменить несохранённые изменения?")) text = original; break;
                    case "8":
                        var insertLines = TextEdits.Lines(text); ShowLines(insertLines);
                        var position = ui.Read($"Вставить перед строкой 1–{insertLines.Count}; {insertLines.Count + 1} — в конец");
                        if (!int.TryParse(position, out var insertAt)) throw new ArgumentException();
                        text = TextEdits.InsertLine(text, insertAt - 1, ui.Read("Новая строка")); break;
                    case "9":
                        var deleteLines = TextEdits.Lines(text); ShowLines(deleteLines);
                        var deleteLine = ui.Index(deleteLines.Count);
                        if (ui.Confirm("Удалить строку " + (deleteLine + 1) + " из буфера?")) text = TextEdits.DeleteLine(text, deleteLine);
                        break;
                    case "10":
                        var find = ui.Read("Найти (с учётом регистра)"); var replacement = ui.Read("Заменить на (можно пустую строку)");
                        if (ui.Confirm("Заменить все точные совпадения в буфере?")) text = TextEdits.Replace(text, find, replacement);
                        break;
                    default: ui.Line("Неизвестный пункт меню."); break;
                }
            }
            catch (OperationCanceledException) { ui.Line("Отменено. Буфер не изменён."); }
            catch (ArgumentException) { ui.Line("Превышен предел ввода или неверное значение. Буфер не изменён."); }
        }
    }
    private void ShowLines(IReadOnlyList<string> lines)
    { for (var i = 0; i < lines.Count; i++) ui.Line($"{i + 1,4} | {lines[i]}"); }
    private async Task Archives()
    {
        ui.Line("1. ZIP в личном хранилище  2. ZIP внешнего объекта по пути  0. Назад");
        switch (ui.Read("Выбор")) { case "1": await Personal(); break; case "2": await ExternalZip(ui.Read("Полный путь источника или ZIP")); break; case "0": break; default: ui.Line("Неизвестный пункт меню."); break; }
    }
    private void Preview(ArchivePreview preview)
    { ui.Line(preview.Summary + "\n" + preview.Warning); ui.Table(preview.Entries.Select(x => new[] { x.Path, x.Directory ? "Папка" : "Файл", x.SizeLabel, x.CompressedLabel, x.Warning }), "Путь", "Тип", "Размер", "Сжатый", "Предупреждение"); }
    private sealed class ArchiveReporter(ConsoleUi ui) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress p) => ui.Line($"ZIP: {p.Completed}/{p.Total}; {FileDisplay.Bytes(p.Bytes)}"); }
    private async Task PersonalZip(string path, FileEntry[] rows)
    {
        var archive = Get<IArchiveService>();
        ui.Line("1. Создать ZIP из выбранных объектов  2. Просмотр ZIP  3. Распаковать ZIP  0. Назад");
        var choice = ui.Read("Выбор"); if (choice == "0") return;
        if (choice == "1")
        {
            var numbers = ui.Read("Номера через запятую").Split(','); var ids = new List<Guid>();
            foreach (var number in numbers) { if (!int.TryParse(number.Trim(), out var n) || n < 1 || n > rows.Length || rows[n - 1].Id is null) throw new ArgumentException(); ids.Add(rows[n - 1].Id!.Value); }
            var name = ui.Read("Имя нового ZIP"); await Done(ct => archive.CreateArchiveAsync(ids, path, name, new ArchiveReporter(ui), ct)); return;
        }
        if (choice is not ("2" or "3")) { ui.Line("Неизвестный пункт меню."); return; }
        var id = rows[ui.Index(rows.Length)].Id ?? throw new ArgumentException();
        var preview = await Run(ct => archive.PreviewArchiveAsync(id, ct)); if (!preview.Succeeded) return; Preview(preview.Value!);
        if (choice == "3" && preview.Value!.CanExtract) { var name = ui.Read("Имя новой папки в текущем каталоге"); if (ui.Confirm("Распаковать в новую папку без перезаписи?")) await Done(ct => archive.ExtractArchiveAsync(id, path, name, preview.Value.Version, new ArchiveReporter(ui), ct)); }
    }
    private async Task ExternalZip(string source)
    {
        ui.Line("1. Создать ZIP  2. Просмотреть ZIP  3. Распаковать ZIP  0. Назад"); var choice = ui.Read("Выбор");
        if (choice == "0") return;
        ArchivePreview? preview = null;
        if (choice is "2" or "3") { var result = await Run(ct => External.PreviewAsync(source, ct)); if (!result.Succeeded) return; preview = result.Value!; Preview(preview); if (choice == "2" || !preview.CanExtract) return; }
        if (choice is not ("1" or "3")) { ui.Line("Неизвестный пункт меню."); return; }
        var directory = ui.Read("Полный путь папки назначения"); var name = ui.Read(choice == "1" ? "Имя нового ZIP" : "Имя новой папки");
        if (ui.Confirm("Выполнить ZIP-операцию без перезаписи?")) await Done(ct => External.ExecuteAsync(new(choice == "1" ? ExternalChange.CreateZip : ExternalChange.ExtractZip, source, directory, name, preview?.Version), ct));
    }
    private async Task Journal()
    {
        var query = new JournalQuery();
        while (true)
        {
            var result = await Get<IJournalService>().GetAsync(query);
            if (!result.IsSuccess) { ui.Line("Журнал недоступен или фильтры некорректны."); return; }
            ui.Line($"Журнал: страница {query.Page + 1}");
            ui.Table(result.Value!.Items.Select(AuditListItem.From).Select(x => new[] { x.OccurredAt, x.Actor, x.Action, x.Target, x.Result, x.Description }), "Время", "Кто", "Действие", "Объект", "Результат", "Описание");
            ui.Line("1. Далее  2. Назад  3. Фильтры  4. Сброс  5. Порядок времени  0. Выход");
            switch (ui.Read("Выбор"))
            {
                case "0": return;
                case "1": if (result.Value.HasMore && query.Page < 10000) query = query with { Page = query.Page + 1 }; break;
                case "2": query = query with { Page = Math.Max(0, query.Page - 1) }; break;
                case "3":
                    var search = ui.Read("Поиск");
                    ui.Line(string.Join(", ", Enum.GetNames<OperationType>()));
                    var type = ui.Read("Тип (пусто — все)"); var outcome = ui.Read("Success/Denied/Failed (пусто — все)");
                    var from = ui.Read("От UTC ISO 8601 (пусто — без границы)"); var until = ui.Read("До UTC ISO 8601, не включая (пусто — без границы)");
                    if (type != "" && (!Enum.TryParse<OperationType>(type, out var t) || !Enum.IsDefined(t)) || outcome != "" && (!Enum.TryParse<OperationResult>(outcome, out var o) || !Enum.IsDefined(o))) throw new ArgumentException();
                    query = new(type == "" ? null : Enum.Parse<OperationType>(type), outcome == "" ? null : Enum.Parse<OperationResult>(outcome), Date(from), Date(until), search); break;
                case "4": query = new(); break;
                case "5": query = query with { Page = 0, Descending = !query.Descending }; break;
                default: ui.Line("Неизвестный пункт меню."); break;
            }
        }
    }
    private static DateTime? Date(string input) => input == "" ? null : DateTimeOffset.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date.UtcDateTime : throw new ArgumentException();
    private async Task Settings()
    {
        ui.Line($"Пользователь: {Session.Current!.Username}\nStorageRoot: {StorageConfiguration.LoadRoot()}\nВерсия: {typeof(ConsoleApplication).Assembly.GetName().Version}\nТекст: 10 МиБ; импорт: 100 МиБ. ZIP: 250 записей, 25 МиБ/файл, 100 МиБ всего, ratio 100:1.\nВнешние операции: 1000 объектов, 100 МиБ/файл, 1 ГиБ всего.\n1. Проверить обязательный аудит  2. Диагностика личных файлов  0. Назад");
        switch (ui.Read("Выбор"))
        {
            case "1": await Get<IJournalService>().CheckHealthAsync(); ui.Line("Состояние аудита: " + Get<IAuditHealth>().Snapshot.State); break;
            case "2": var result = await Run(ct => Get<IStorageDiagnostics>().DiagnoseAsync(ct)); if (result.Succeeded) { ui.Line($"Проверено: {result.Value!.CheckedObjects}; полнота: {result.Value.Complete}"); ui.Table(result.Value.Issues.Select(x => new[] { x.Code, x.Path ?? "—" }), "Код", "Путь"); } break;
            case "0": break;
            default: ui.Line("Неизвестный пункт меню."); break;
        }
    }
}
