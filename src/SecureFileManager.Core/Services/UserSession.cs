using SecureFileManager.Core.Interfaces;
namespace SecureFileManager.Core.Services;

public sealed class UserSession : IUserSession
{
    private SessionIdentity? _current;
    private long _revision;
    public SessionIdentity? Current => Volatile.Read(ref _current);
    public long Revision => Interlocked.Read(ref _revision);
    public event EventHandler? Changed;
    internal void Start(SessionIdentity identity) { Volatile.Write(ref _current, identity); Interlocked.Increment(ref _revision); Changed?.Invoke(this, EventArgs.Empty); }
    internal void End() { Volatile.Write(ref _current, null); Interlocked.Increment(ref _revision); Changed?.Invoke(this, EventArgs.Empty); }
}
public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }
