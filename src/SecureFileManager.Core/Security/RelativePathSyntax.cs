namespace SecureFileManager.Core.Security;

public static class RelativePathSyntax
{
    public static bool IsValid(string? path) => path is { Length: > 0 and <= 400 } && path.Split('/').All(IsNameValid);
    public static bool IsNameValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd();
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
            "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
