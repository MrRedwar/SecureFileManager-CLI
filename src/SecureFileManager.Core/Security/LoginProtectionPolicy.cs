namespace SecureFileManager.Core.Security;

public sealed class LoginProtectionPolicy
{
    public int MaximumFailures { get; }
    public TimeSpan ObservationWindow { get; }
    public TimeSpan LockoutDuration { get; }
    public LoginProtectionPolicy(int maximumFailures = 5, TimeSpan? observationWindow = null, TimeSpan? lockoutDuration = null)
    {
        MaximumFailures = maximumFailures;
        ObservationWindow = observationWindow ?? TimeSpan.FromMinutes(15);
        LockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(15);
        if (MaximumFailures is < 1 or > 100 || ObservationWindow <= TimeSpan.Zero || LockoutDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumFailures), "Недопустимая политика блокировки.");
    }
}
