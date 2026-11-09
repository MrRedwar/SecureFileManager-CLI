using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SecureFileManager.Core.Interfaces;
namespace SecureFileManager.Core.Security;

public sealed class PasswordSecurity : IPasswordSecurity
{
    private readonly PasswordHasher<object> _hasher;
    private readonly string _dummy;
    private readonly object _subject = new();
    public PasswordSecurity(int iterations = 600_000)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = iterations }));
        _dummy = _hasher.HashPassword(_subject, Guid.NewGuid().ToString("N"));
    }
    public string Hash(string password) => _hasher.HashPassword(_subject, password);
    public PasswordCheck Verify(string hash, string password)
    {
        try
        {
            return _hasher.VerifyHashedPassword(_subject, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.RehashNeeded,
                _ => PasswordCheck.Failed
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        { return PasswordCheck.Failed; }
    }
    public void VerifyDummy(string password) => Verify(_dummy, password);
}
