# Реальные проверки — 09.11.2026

## Дополнение 11.11.2026 — редактор и кириллица

Исправлены интерактивные Unicode-ввод/вывод Windows-консоли и редактирование существующего буфера. Меню: 2 — изменение строки с текущим текстом, 7 — полная замена, 8/9 — вставка/удаление строк, 10 — замена фрагмента. Для файлов используются прежние сервисы и валидация; уже повреждённые символы автоматически не восстанавливаются.

`./scripts/Test-CLI.ps1 -Sql`: **188 PASS, 0 FAIL, 0 SKIP**, 59 секунд; финальный TRX `artifacts/tests/5d2f767c74ed49e7aae7ae5c3fb2fb64/tests.trx`. Проверены сохранность нетронутых строк и смешанных CRLF/LF/CR, UTF-8/кириллица, лимит буфера до расширения замены, ошибочные индексы. Реальный консольный сценарий на временных SQL/файлах включает отмену изменения строки, просмотр, вставку, удаление, замену фрагмента, редактирование русской строки, сохранение и ZIP round-trip.

`./scripts/Test-ConsoleUnicode.ps1`: **PASS**. В отдельной скрытой Windows-консоли с CP866 проверены нативные ReadConsoleW и WriteConsoleW, строка «Египет не тот — ёж», изменение подставленного текста клавишами Home/End. Проверка не использует SQL или пользовательские файлы. Исходники проверки — tests/ConsoleUnicodeProbe; запуск отдельным скриптом, не дополнительный SQL-тест решения. Первый вспомогательный probe имел неверную ANSI-разметку тестовых клавиатурных событий; разметка исправлена, итоговые проверки выполнены с Unicode-событиями.

Debug build и сборка Release-публикации — PASS без предупреждений/ошибок. Core/Data/Windows-файлы: 74/74 хеша совпадают с REUSE_MANIFEST. Рабочая БД/хранилище не изменялись. Полный ручной прогон в окне Visual Studio пользователя после обновления остаётся открытым; нативная проверка проведена в отдельной скрытой консоли.

Ниже сохранены результаты первоначальной реализации от 09.11.2026.

## Среда и границы

Windows 10 19045 x64, SDK 9.0.200/MSBuild 17.13.8, runtime 9.0.2, Visual Studio 2022 17.13.35818.85; работающий локальный SQLEXPRESS. Сборке в изолированном агенте потребовался разрешённый доступ к установленному SDK/NuGet. Версии платформы/пакетов не обновлялись.

Рабочая SecureFileManagerDB использовалась **только для чтения**: preflight и SELECT определений CHECK для отладки сравнения. В рабочую БД не выполнялись регистрация/вход/audit probe/миграция/очистка/запись. Реальные пользовательские файлы не редактировались и не переносились. Source WPF Git status до и после — чистый.

## Итоговые результаты

| Проверка | Фактический результат |
|---|---|
| Debug solution build | PASS, 0 предупреждений / 0 ошибок в итоговой сборке |
| Release solution build | PASS, 0 предупреждений / 0 ошибок |
| Полный Debug test, SQL opt-in | **185 passed, 0 failed, 0 skipped**, около 40 секунд |
| Новый Test-CLI.ps1 без SQL | **79 passed, 0 failed, 106 skipped**, всего 185; пропуски ожидаемые SQL opt-in |
| Framework-dependent publish Release | PASS, artifacts/publish/win-x64, без WPF и локального JSON |
| Рабочая SQL/схема/StorageRoot `--check` | PASS, только чтение |
| Реальный `dotnet run` Development + ввод `0` | PASS: preflight, русское начальное меню, выход с кодом 0 |
| Побайтовое сравнение Core/Data | 64/64 совпадают, REUSE_MANIFEST.json |
| Windows-адаптеры | 10/10 совпадают после единственной замены namespace |
| Проверка исходного репозитория | Чистый status; рабочие файлы/Git не менялись |

Финальный полный TRX после проверки восстановления: `artifacts/tests/45d13abfb99644dfb526aabd1951b265/tests.trx` (185/185, 39 секунд). Предыдущий полный TRX: `artifacts/tests/final-debug.trx`. Скриптовый unit-only TRX: `artifacts/tests/91f1710612cd44859f42e1121bdb102e/tests.trx`. Артефакты игнорируются Git. Старые `integration.trx`/`preflight-fix.trx` сохраняют промежуточные результаты, не заменяют итоговый отчёт.

Полный запуск:

```powershell
$env:SFM_RUN_SQL_TESTS = '1'
$env:SFM_ENVIRONMENT = 'Development'
$env:SFM_EXISTING_TEST_DATABASE = $null
# SFM_STORAGE_TEST_ROOT задан новым временным GUID-каталогом.
dotnet test SecureFileManager-CLI.sln -c Debug --logger 'trx;LogFileName=final-debug.trx' --results-directory artifacts/tests
```

Финальный повтор выполнен через `./scripts/Test-CLI.ps1 -Sql` с полной изоляцией конфигурации. SQL-fixture создаёт новый SecureFileManagerTests_<32 hex GUID>, предварительно проверяет отсутствие имени, применяет исходные миграции только туда и удаляет только собственную БД. SFM_EXISTING_TEST_DATABASE теперь запрещён. Файловые тесты используют GUID-каталоги; часть артефактов оставлена во временном каталоге для диагностики. Рекурсивная очистка произвольных папок не выполнялась.

## Что покрыто

Перенесённые проверки: Identity KDF/соль/rehash, правила логина/lockout, owner/isolation, SQL constraints/RowVersion, файловые transactions/rollback/ambiguous commit/recovery, junction/hardlink/path rejection, import/размеры/encoding/BOM, TXT/JSON/XML/XXE, безопасный ZIP Slip/Bomb/CRC/actual bytes/cancellation, external CRUD/tree copy/move/conflicts, trash/restore, аудит/фильтры/страницы/диагностика, конфигурация и диски.

Шесть новых CliTests:

- MenuRejectsInvalidInputAndExitsWithoutOpeningSql — меню, ошибочный выбор, отмена, выход без SQL.
- TerminalNeutralizesControlAndBidiCharacters — безопасный вывод терминала.
- MaskedPasswordHandlesBackspaceEscapeAndNeverEchoesSecret — логика ввода по ConsoleKeyInfo, маскировка, Backspace, Esc; реальная клавиатура отдельно не проверена.
- RealConsoleFlowUsesExistingServicesForIdentityEditorZipJournalAndLogout — скрипт меню над реальной временной SQL/Windows: регистрация, вход, create/edit/validate/save TXT, ZIP create/preview/extract, журнал, диагностика, logout; проверены физический текст и SQL audit.
- ExternalConsoleFlowEditsCopiesRenamesTrashesRestoresAndArchives — JSON редактор, copy, rename, trash, restore, ZIP create/preview/extract через меню; проверены результаты и аудит.
- PreflightIsReadOnlyRejectsWrongStorageAndChangedSchema — исходная схема принимается без audit INSERT, неверный root отклоняется без создания каталога, удалённый индекс выявляется. Дополнительно имитируется publish личного Delete до SQL commit: preflight предупреждает без записи, а меню явно восстанавливает исходный файл через RecoverAsync. Изменение схемы/имитация crash только в собственных тестовых ресурсах.

В сценарных CLI-тестах терминал подставляет ввод/пароли в методы UI; сервисы, SQL и Windows-адаптеры реальные. Это не native TTY-тест. GUI/WPF tests не переносились и не выполнялись. 185 — актуальный набор CLI, его нельзя напрямую приравнивать к историческим 194 WPF cases.

## Найденное и исправленное

Первый полный прогон: 182 passed / 1 failed. Preflight ошибочно сравнивал SQL type timestamp с EF rowversion; SQL Server также разворачивает IN/BETWEEN в CHECK definition. Исправлена нормализация в новом CLI-компоненте; исходные Core/Data/миграции не затронуты. Целевой повтор preflight PASS; окончательный полный прогон с дополнительными CLI-тестами — 185/185.

## Не выполнено

Реальный ввод пароля клавиатурой в Windows Terminal/консоли VS, Ctrl+C на длительной **консольной** операции (сервисная отмена покрыта), WPF→CLI→WPF под существующим аккаунтом с записью, чистая машина, полный межпроцессный WPF+CLI contention/crash/recovery, физические removable media/disk full/power loss. Эти проверки требуют пользовательской приёмки/изолированного стенда. В рабочую БД для демонстрации не входили, чтобы не менять историю/счётчики/хеши. Одновременная эксплуатация не объявлена безопасной.

В Release выполнена сборка/публикация; отдельный повтор всех тестов в Release не выполнялся. Полный тестовый прогон — Debug.
