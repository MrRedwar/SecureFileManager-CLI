using SecureFileManager.Core.Interfaces;
using SecureFileManager.Core.Security;
using SecureFileManager.Core.Results;

namespace SecureFileManager.Tests;

public sealed class PasswordTests
{
    private sealed class PendingAuthentication : IAuthenticationService, IRegistrationService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<AuthOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AuthOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
        { Calls++; return Completion.Task; }
        public Task<AuthOutcome> LogoutAsync(CancellationToken ct = default) => Task.FromResult(AuthOutcome.Ok());
        public Task<AuthOutcome> RegisterAsync(string username, string password, string confirmation, CancellationToken ct = default) => Completion.Task;
    }
    [Theory]
    [InlineData("ab", false)]
    [InlineData("normal-user", true)]
    [InlineData("name with spaces", false)]
    [InlineData(" user", false)]
    [InlineData("имя-пользователя", true)]
    public void UsernameValidation(string username, bool valid) => Assert.Equal(valid, IdentityValidation.ValidUsername(username));
}
