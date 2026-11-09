using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Files;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Results;


namespace SecureFileManager.Tests;

public sealed class ComputerBrowserTests
{
    private sealed class Session : IUserSession
    {
        public SessionIdentity? Current { get; private set; } = new(Guid.NewGuid(), "test-only");
        public long Revision { get; private set; }
        public event EventHandler? Changed;
        public void Switch() { Current = new(Guid.NewGuid(), "second"); Revision++; Changed?.Invoke(this, EventArgs.Empty); }
        public void End() { Current = null; Revision++; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private sealed class Users : IUserRepository
    {
        public RepositoryStatus Status { get; set; } = RepositoryStatus.Success;
        public bool Active { get; set; } = true;
        public Task<RepositoryResult<User>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status == RepositoryStatus.Success ? RepositoryResult<User>.Ok(new() { Id = id, IsActive = Active }) : RepositoryResult<User>.Fail(Status));
        public Task<RepositoryResult<User>> AddAsync(User user, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepositoryResult<User>> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Reader(Action? action = null) : IComputerDirectoryReader
    {
        public int Calls { get; private set; }
        public ComputerDirectory List(string path, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; action?.Invoke(); return new(path, "", [new(path + "\\existing.txt", "existing.txt", false, 10, DateTime.UtcNow, false)]); }
    }
    private sealed class Audit(Action? action = null) : IAuditService
    {
        public bool Available { get; set; } = true;
        public List<(Guid? Actor, OperationResult Result, string Code)> Events { get; } = [];
        public Task<bool> RecordAsync(SessionIdentity? actor, OperationType type, OperationResult result, string code, CancellationToken ct = default)
        { action?.Invoke(); Events.Add((actor?.Id, result, code)); return Task.FromResult(Available); }
    }
    [Fact]
    public async Task ExternalListIsSharedAndAuditIdentifiesActorWithoutLeakingAbsolutePath()
    {
        var session = new Session(); var audit = new Audit(); var reader = new Reader();
        var service = new ComputerBrowseService(session, new Users(), reader, audit);
        var firstId = session.Current!.Id;
        var first = await service.ListAsync(@"C:\test-only"); session.Switch();
        var second = await service.ListAsync(@"C:\test-only");
        Assert.True(first.Succeeded); Assert.True(second.Succeeded);
        Assert.Equal(first.Value!.Entries[0].Path, second.Value!.Entries[0].Path);
        Assert.Equal(firstId, audit.Events[0].Actor); Assert.Equal(session.Current!.Id, audit.Events[1].Actor);
        Assert.All(audit.Events, e => Assert.Equal("COMPUTER_DIRECTORY_LISTED", e.Code));
    }
    [Fact]
    public async Task MissingSessionInactiveUserAndMissingSqlNeverEnumerateComputer()
    {
        var session = new Session(); var users = new Users(); var reader = new Reader();
        var service = new ComputerBrowseService(session, users, reader, new Audit());
        users.Status = RepositoryStatus.InfrastructureError;
        Assert.Equal(FileStatus.DatabaseUnavailable, (await service.ListAsync(@"C:\")).Status);
        users.Status = RepositoryStatus.Success; users.Active = false;
        Assert.Equal(FileStatus.Denied, (await service.ListAsync(@"C:\")).Status);
        session.End(); Assert.Equal(FileStatus.Denied, (await service.ListAsync(@"C:\")).Status);
        Assert.Equal(0, reader.Calls);
    }
    [Fact]
    public async Task AuditFailureAndSessionSwitchDoNotReturnDirectoryContents()
    {
        var session = new Session(); var audit = new Audit { Available = false };
        var failed = await new ComputerBrowseService(session, new Users(), new Reader(), audit).ListAsync(@"C:\");
        Assert.Equal(FileStatus.DatabaseUnavailable, failed.Status); Assert.Null(failed.Value);
        var switched = await new ComputerBrowseService(session, new Users(), new Reader(session.Switch), new Audit()).ListAsync(@"C:\");
        Assert.Equal(FileStatus.Denied, switched.Status); Assert.Null(switched.Value);
        var switchedDuringAudit = await new ComputerBrowseService(session, new Users(), new Reader(), new Audit(session.Switch)).ListAsync(@"C:\");
        Assert.Equal(FileStatus.Denied, switchedDuringAudit.Status); Assert.Null(switchedDuringAudit.Value);
    }
    private sealed class Delayed : IComputerBrowseService
    {
        public TaskCompletionSource<FileResult<ComputerDirectory>> Pending { get; } = new();
        public Task<FileResult<ComputerDirectory>> ListAsync(string path, CancellationToken ct = default) => Pending.Task;
    }
}
