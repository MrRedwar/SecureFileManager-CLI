using SecureFileManager.Cli;
using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Journal;
using SecureFileManager.Core.Results;


namespace SecureFileManager.Tests;
public sealed class JournalTests
{
    private sealed class Clock : IClock { public DateTime UtcNow { get; set; } = DateTime.UtcNow; }
    private sealed class Probe(Func<Task<bool>> action) : IAuditProbe { public Task<bool> CheckAsync(CancellationToken ct = default) => action(); }
    [Fact]
    public void JournalProjectionNeverDisplaysRawExceptionDetailsOrAbsolutePaths()
    {
        var source = new Operation { Details = "PasswordHash=SECRET; connection=SECRET; document=SECRET", TargetPathSnapshot = @"C:\private\SECRET",
            OperationType = OperationType.OPERATION_FAILED, Result = OperationResult.Failed };
        var item = AuditListItem.From(source); Assert.DoesNotContain("SECRET", item.Description); Assert.Equal("—", item.Target);
        Assert.Equal("Failed", item.ResultCode);
        source.TargetPathSnapshot = "owned/name.txt"; Assert.Equal("owned/name.txt", AuditListItem.From(source).Target);
    }
    private sealed class DelayedJournal : IJournalService
    {
        public TaskCompletionSource<RepositoryResult<JournalPage>> Pending { get; } = new();
        public Task CheckHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RepositoryResult<JournalPage>> GetAsync(JournalQuery query, CancellationToken ct = default) => Pending.Task;
    }
    private sealed class FailedJournal : IJournalService
    {
        public Task CheckHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RepositoryResult<JournalPage>> GetAsync(JournalQuery query, CancellationToken ct = default) =>
            Task.FromResult(RepositoryResult<JournalPage>.Fail(RepositoryStatus.InfrastructureError));
    }
    [Fact]
    public void BillionLaughsRejectedBeforeExpansion()
    {
        var result = Core.Files.DocumentValidator.Validate(Core.Files.DocumentFormat.Xml,
            "<!DOCTYPE lolz [<!ENTITY lol 'lol'><!ENTITY lol1 '&lol;&lol;&lol;&lol;'><!ENTITY lol2 '&lol1;&lol1;&lol1;&lol1;'>]><r>&lol2;</r>");
        Assert.True(result.Prohibited); Assert.Equal("XML_DTD", result.Code);
    }
}
