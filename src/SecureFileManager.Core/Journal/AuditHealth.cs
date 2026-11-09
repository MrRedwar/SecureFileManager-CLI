using SecureFileManager.Core.Interfaces;

namespace SecureFileManager.Core.Journal;

// Point-in-time evidence, never a promise of continuous availability.
public sealed class AuditHealth(IClock clock) : IAuditHealth
{
    private readonly object _sync = new();
    private AuditHealthSnapshot _snapshot = new(AuditState.Unknown, null);
    private long _version;
    public event EventHandler? Changed;
    public AuditHealthSnapshot Snapshot
    {
        get
        {
            lock (_sync) return _snapshot.State == AuditState.Active &&
                clock.UtcNow - _snapshot.CheckedUtc > TimeSpan.FromMinutes(2) ?
                _snapshot with { State = AuditState.Unknown } : _snapshot;
        }
    }
    private void Notify()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
            try { handler(this, EventArgs.Empty); } catch { /* UI must not invalidate a committed transaction. */ }
    }
    public void Succeeded()
    {
        lock (_sync)
        {
            // A successful failure-event insert must not hide a known fault in mandatory auditing.
            _version++;
            if (_snapshot.State != AuditState.Unavailable) _snapshot = new(AuditState.Active, clock.UtcNow);
        }
        Notify();
    }
    public void Failed() { lock (_sync) { _version++; _snapshot = new(AuditState.Unavailable, clock.UtcNow); } Notify(); }
    public void Reset() { lock (_sync) { _version++; _snapshot = new(AuditState.Unknown, null); } Notify(); }
    public async Task ProbeAsync(IAuditProbe probe, CancellationToken ct)
    {
        long version;
        lock (_sync) { version = ++_version; _snapshot = new(AuditState.Checking, _snapshot.CheckedUtc); }
        Notify();
        bool? result = null;
        try { result = await probe.CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { result = false; }
        lock (_sync)
        {
            // A concurrent real write failure takes precedence over an older probe.
            if (_version != version) return;
            _version++; _snapshot = new(result is null ? AuditState.Unknown : result.Value ? AuditState.Active : AuditState.Unavailable,
                result is null ? null : clock.UtcNow);
        }
        Notify();
    }
}
