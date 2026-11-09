namespace SecureFileManager.Core.Security;

public static class IdentityValidation
{
    public static bool ValidUsername(string? value) => value is { Length: >= 3 and <= 50 } &&
        value == value.Trim() && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    public static bool ValidPassword(string? value) => value is { Length: >= 10 and <= 128 } && !string.IsNullOrWhiteSpace(value);
}
