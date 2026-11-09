# Карта повторного использования

Исходник: `D:\SecureFileManager`, актуальная Windows-версия с расширениями 11.01–11.04. Исходная папка использовалась только для чтения. Приоритет отдан коду и актуальным дополнениям, а не старым планам, где внешний режим ещё отсутствовал. Изучены структура solution/пакетов, Core/Data, WPF composition root, адаптеры, UI-потоки, тестовые fixtures и актуальные документы хранения/внешних операций/ZIP/аудита. Исторические планы WPF не перенесены как требования нового интерфейса.

## Перенос

| Источник | Назначение | Изменение |
|---|---|---|
| Все 40 файлов Core, включая csproj | src/SecureFileManager.Core | Нет, SHA-256 совпадает |
| Все 24 файлов Data, включая csproj, 3 миграции и snapshot | src/SecureFileManager.Data | Нет, SHA-256 совпадает |
| PersonalStorage, StorageConfiguration | Infrastructure.Windows/FileSystem | Только namespace |
| SafeNativeFile | Infrastructure.Windows/FileSystem | Только namespace, P/Invoke сохранён |
| WindowsFileStorage и WindowsFileStorage.Prepared | Infrastructure.Windows/FileSystem | Только namespace |
| WindowsComputerDirectoryReader, WindowsDriveService | Infrastructure.Windows/FileSystem | Только namespace |
| WindowsExternalDocumentStorage | Infrastructure.Windows/FileSystem | Только namespace |
| WindowsExternalOperationStorage и .ArchivesTrash | Infrastructure.Windows/FileSystem | Только namespace |
| AuditListItem record из JournalViewModel.cs | Cli/AuditListItem.cs | Извлечён record, сменены using/namespace, VM не переносилась |
| App.CreateServices | Cli/Composition.cs | Взята сервисная DI-регистрация, удалены UI-регистрации, добавлены CLI/preflight |
| global.json | Корень CLI | Без изменений |
| appsettings.Development.json | Локальный ignored CLI-конфиг | Скопирован; собственного обращения к исходной папке нет |

Побайтовая карта и хеши 74 перенесённых файлов: [REUSE_MANIFEST.json](REUSE_MANIFEST.json). Для десяти Windows-файлов также подтверждено равенство текста после единственной замены namespace. Подсчёт 40/24 относится к файлам, а не только к классам.

## Сохранённые сервисы

Регистрация/вход/выход, IdentityV3 PasswordSecurity с солью/версионированием/условным rehash и прежний lockout не заменены. CLI не выполнял вход в рабочую БД и не изменял рабочие хеши; при реальном входе поведение условного rehash остаётся поведением исходного AuthenticationService.

FileService/UserStorageService и их контракты сохраняют owner, UserId, staging, metadata transactions, recovery, import и синхронизацию. ExternalDocumentService и ExternalFileOperationService сохраняют Windows-ограничения, общий внешний режим, RowVersion, rollback, trash receipts, архивирование. ZipEngine/DocumentValidator/JournalService не изменены.

Не перенесены Views, XAML, ViewModels, WPF App, converters, графические assets, WindowsFileDialogs и UI-порты диалогов. Для ConsoleApplication написаны только ввод/вывод, навигация, буфер редактора и вызовы сервисов. Новых файловых алгоритмов или репозиториев нет.

## Тесты

Перенесены существующие сервисные/SQL/Windows/ZIP/document/config тесты. Ссылки на Windows-адаптеры переименованы; AuditListItem проверяется из CLI. WpfShellTests и NavigationTests исключены целиком. Методы с WPF VM (формы, dirty/editor windows, поздние VM-результаты, shell logout) исключены из смешанных файлов вместе с IFileDialogs helpers; их GUI assertions не выдаются за CLI-проверки. Самостоятельные проверки Hash/IdentityValidation и остальные service assertions сохранены. Общие неиспользуемые test doubles не являются production-компонентами.

SqlDatabaseFixture адаптирован только для усиления изоляции: существующие базы запрещены даже при SFM_EXISTING_TEST_DATABASE, имя всегда генерируется fixture. Начальное существование имени блокирует запуск; cleanup только при собственном создании. Миграции запускаются только внутри этого тестового fixture.

Новые CliTests проверяют меню/неверный ввод/отмену/выход, экранирование терминала, маскированный ввод и сервисные сценарии консоли, preflight без записи и отказ на неправильном root/индексе. Скрипт Test-CLI.ps1 задаёт отдельное окружение и временные ресурсы.

## Новые файлы

CLI: Program.cs, Composition.cs, ConsoleApplication.cs, ConsoleUi.cs, StartupCheck.cs, извлечённый AuditListItem.cs, csproj, example/local settings, Properties/launchSettings.json. Infrastructure: новый csproj. Solution/global.json/.gitignore, scripts/Test-CLI.ps1, CliTests.cs, README и документы docs. Полный список файлов нового репозитория — [FILE_INVENTORY.txt](FILE_INVENTORY.txt).
