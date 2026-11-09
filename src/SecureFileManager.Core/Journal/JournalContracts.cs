using SecureFileManager.Core.Entities;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Core.Journal;

public sealed record JournalQuery(OperationType? Type = null, OperationResult? Result = null,
    DateTime? FromUtc = null, DateTime? UntilUtc = null, string Search = "", bool Descending = true,
    int Page = 0, int PageSize = 50)
{
    public bool IsValid => (Type is null || Enum.IsDefined(Type.Value)) && (Result is null || Enum.IsDefined(Result.Value)) &&
        (FromUtc is null || FromUtc.Value.Kind == DateTimeKind.Utc) && (UntilUtc is null || UntilUtc.Value.Kind == DateTimeKind.Utc) &&
        !(FromUtc >= UntilUtc) && Search is { Length: <= 200 } && !Search.Any(char.IsControl) &&
        Page is >= 0 and <= 10000 && PageSize is >= 1 and <= 100;
}
public sealed record JournalPage(IReadOnlyList<Operation> Items, bool HasMore);
public interface IJournalService
{
    Task<RepositoryResult<JournalPage>> GetAsync(JournalQuery query, CancellationToken ct = default);
    Task CheckHealthAsync(CancellationToken ct = default);
}
public interface IAuditProbe { Task<bool> CheckAsync(CancellationToken ct = default); }
public enum AuditState { Unknown, Checking, Active, Unavailable }
public sealed record AuditHealthSnapshot(AuditState State, DateTime? CheckedUtc);
public interface IAuditHealth
{
    AuditHealthSnapshot Snapshot { get; }
    event EventHandler? Changed;
    void Succeeded();
    void Failed();
}
