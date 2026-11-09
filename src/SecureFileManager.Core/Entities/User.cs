namespace SecureFileManager.Core.Entities;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? FailedLoginWindowStartUtc { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
}
