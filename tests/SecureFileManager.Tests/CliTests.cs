using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFileManager.Cli;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Files;
using SecureFileManager.Data;
using SecureFileManager.Infrastructure.Windows.FileSystem;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SecureFileManager.Tests;

public sealed class ScriptTerminal(IEnumerable<string> input, IEnumerable<string>? passwords = null) : ConsoleUi
{
    private readonly Queue<string> lines = new(input);
    private readonly Queue<string> secrets = new(passwords ?? []);
    public StringBuilder Output { get; } = new();
    public override void Write(string text) => Output.Append(Safe(text));
    public override string Read(string prompt)
    {
        if (lines.Count == 0) throw new EndOfStreamException();
        var value = lines.Dequeue();
        if (value == "/cancel") throw new OperationCanceledException();
        return value;
    }
    public override string Password(string prompt) => secrets.Dequeue();
    public override string EditLine(string prompt, string initial) => Read(prompt);
}

[Collection("SQL Server")]
public sealed class CliTests
{
    private const string Password = "CLI test-only password 2026!";
    private sealed class Factory(string connection) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options); }
    private static ServiceProvider Provider(string connection, string root)
    {
        // Compose the real graph; replace only configuration/factory with owned test resources.
        var services = Composition.Create();
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(new Factory(connection));
        services.RemoveAll<PersonalStorage>(); services.AddSingleton(new PersonalStorage(root));
        services.RemoveAll<ConfiguredStorageRoot>(); services.AddSingleton(new ConfiguredStorageRoot(root));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    [Fact]
    public async Task MenuRejectsInvalidInputAndExitsWithoutOpeningSql()
    {
        using var services = Provider(@"Server=.\SQLEXPRESS;Database=SecureFileManagerTests_unreachable;Integrated Security=True;Encrypt=True", Path.Combine(Path.GetTempPath(), "sfm-cli-" + Guid.NewGuid().ToString("N")));
        var terminal = new ScriptTerminal(["bad", "/cancel", "0"]);
        await new ConsoleApplication(services, terminal).RunAsync();
        Assert.Contains("Неизвестный пункт", terminal.Output.ToString());
        Assert.Contains("отменены", terminal.Output.ToString());
    }
    [Fact]
    public void TerminalNeutralizesControlAndBidiCharacters()
    { Assert.DoesNotContain('\u001b', ConsoleUi.Safe("name\u001b[31m\u202esecret")); Assert.DoesNotContain('\u202e', ConsoleUi.Safe("\u202e")); }

    [Fact]
    public void MaskedPasswordHandlesBackspaceEscapeAndNeverEchoesSecret()
    {
        var keys = new Queue<ConsoleKeyInfo>([
            new('я', ConsoleKey.A, false, false, false), new('x', ConsoleKey.X, false, false, false),
            new('\b', ConsoleKey.Backspace, false, false, false), new('7', ConsoleKey.D7, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false)]);
        var output = new StringBuilder();
        Assert.Equal("я7", ConsoleUi.ReadMasked(keys.Dequeue, s => output.Append(s)));
        Assert.DoesNotContain("я", output.ToString()); Assert.DoesNotContain("7", output.ToString());
        Assert.Contains("*", output.ToString());
        Assert.Throws<OperationCanceledException>(() => ConsoleUi.ReadMasked(() => new('\u001b', ConsoleKey.Escape, false, false, false), _ => { }));
    }

    [SqlFact]
    public async Task ExternalConsoleFlowEditsCopiesRenamesTrashesRestoresAndArchives()
    {
        var database = new SqlDatabaseFixture(); await database.InitializeAsync();
        try
        {
            var area = Path.Combine(Path.GetTempPath(), "sfm-cli-external-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(area, "Storage"); var external = Path.Combine(area, "External"); Directory.CreateDirectory(external);
            using var provider = Provider(database.ConnectionString, root);
            var name = "external-" + Guid.NewGuid().ToString("N");
            Assert.True((await provider.GetRequiredService<IRegistrationService>().RegisterAsync(name, Password, Password)).Succeeded);
            var note = Path.Combine(external, "note.json"); var archive = Path.Combine(external, "notes.zip");
            var terminal = new ScriptTerminal([
                "2", name, "1", "3", external, "5", "note.json", "1", "1", "7", "{\"ok\":true}", ".end", "3", "4", "0",
                "7", "1", external, "copied.json", "9", "1", "renamed.json", "10", "1", "да", "11", note, "да", "0",
                "4", "2", note, "1", external, "notes", "да",
                "4", "2", archive, "2", "4", "2", archive, "3", external, "extracted", "да", "5", "0", "0"
            ], [Password]);
            await new ConsoleApplication(provider, terminal).RunAsync();
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(note));
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(Path.Combine(external, "renamed.json")));
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(Path.Combine(external, "extracted", "note.json")));
            await using var db = new Factory(database.ConnectionString).CreateDbContext();
            Assert.True(await db.Operations.AnyAsync(x => x.Details == "EXTERNAL_OP_Restore"));
            Assert.True(await db.Operations.AnyAsync(x => x.Details == "EXTERNAL_OP_ExtractZip"));
            Assert.DoesNotContain(Password, terminal.Output.ToString());
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlFact]
    public async Task RealConsoleFlowUsesExistingServicesForIdentityEditorZipJournalAndLogout()
    {
        var database = new SqlDatabaseFixture(); await database.InitializeAsync();
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "sfm-cli-flow-" + Guid.NewGuid().ToString("N"), "Storage");
            using var provider = Provider(database.ConnectionString, root);
            var name = "cli-" + Guid.NewGuid().ToString("N");
            var terminal = new ScriptTerminal([
                "1", name, "2", name,
                "2", "3", "note.txt", "1", "1", "7", "Привет из CLI", ".end",
                "2", "1", "/cancel", "1", "8", "1", "Временная строка", "9", "1", "да",
                "10", "CLI", "консоли", "да", "2", "1", "Египет не тот — русский текст", "1", "3", "4", "0",
                "9", "1", "1", "", "archive", "9", "2", "1", "9", "3", "1", "Extracted", "да", "0",
                "5", "0", "6", "2", "7", "0"
            ], [Password, Password, Password]);
            await new ConsoleApplication(provider, terminal).RunAsync();
            Assert.Null(provider.GetRequiredService<IUserSession>().Current);
            await using var db = new Factory(database.ConnectionString).CreateDbContext();
            var user = await db.Users.SingleAsync(x => x.Username == name);
            var personal = Path.Combine(root, "Users", user.Id.ToString("D"));
            Assert.Equal("Египет не тот — русский текст", await File.ReadAllTextAsync(Path.Combine(personal, "note.txt")));
            Assert.Equal("Египет не тот — русский текст", await File.ReadAllTextAsync(Path.Combine(personal, "Extracted", "note.txt")));
            Assert.Contains("Египет не тот — русский текст", terminal.Output.ToString());
            Assert.True(await db.Operations.AnyAsync(x => x.UserId == user.Id && x.Details == "FILE_TX_SAVE"));
            Assert.True(await db.Operations.AnyAsync(x => x.UserId == user.Id && x.Details == "LOGOUT"));
            Assert.DoesNotContain(Password, terminal.Output.ToString());
        }
        finally { await database.DisposeAsync(); }
    }

    [SqlFact]
    public async Task PreflightIsReadOnlyRejectsWrongStorageAndChangedSchema()
    {
        var database = new SqlDatabaseFixture(); await database.InitializeAsync();
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "sfm-cli-check-" + Guid.NewGuid().ToString("N"), "Storage");
            using var provider = Provider(database.ConnectionString, root);
            var name = "check-" + Guid.NewGuid().ToString("N");
            Assert.True((await provider.GetRequiredService<IRegistrationService>().RegisterAsync(name, Password, Password)).Succeeded);
            await using var db = new Factory(database.ConnectionString).CreateDbContext();
            var before = await db.Operations.CountAsync();
            var checker = provider.GetRequiredService<StartupCheck>();
            var result = await checker.RunAsync(); Assert.True(result.Success, result.Message);
            Assert.Equal(before, await db.Operations.CountAsync());
            var wrong = new StartupCheck(new Factory(database.ConnectionString), new PersonalStorage(root + "-wrong"), new(root + "-wrong"));
            Assert.False((await wrong.RunAsync()).Success); Assert.False(Directory.Exists(root + "-wrong"));
            Assert.True((await provider.GetRequiredService<IAuthenticationService>().LoginAsync(name, Password)).Succeeded);
            Assert.True((await provider.GetRequiredService<IFileService>().CreateAsync("", "pending.txt", false)).Succeeded);
            var owner = provider.GetRequiredService<IUserSession>().Current!.Id;
            using (var workspace = provider.GetRequiredService<IPhysicalFileStorage>().Open(owner))
            {
                var plan = workspace.Stage(Guid.NewGuid(), ChangeKind.Delete, "pending.txt");
                workspace.Publish(plan); // Simulate crash before SQL metadata commit/settlement in owned resources.
            }
            before = await db.Operations.CountAsync();
            var warning = await checker.RunAsync(); Assert.True(warning.Success, warning.Message);
            Assert.Contains("ВНИМАНИЕ", warning.Message); Assert.Equal(before, await db.Operations.CountAsync());
            var terminal = new ScriptTerminal(["2", "да", "0", "0"]);
            await new ConsoleApplication(provider, terminal).RunAsync();
            Assert.True(File.Exists(Path.Combine(root, "Users", owner.ToString("D"), "pending.txt")));
            Assert.Null(provider.GetRequiredService<IUserSession>().Current);
            // DDL is applied exclusively to this fixture's newly generated database.
            await db.Database.ExecuteSqlRawAsync("DROP INDEX [IX_Users_Username] ON [Users]");
            Assert.False((await checker.RunAsync()).Success);
        }
        finally { await database.DisposeAsync(); }
    }
}
