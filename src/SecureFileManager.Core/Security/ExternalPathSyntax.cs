namespace SecureFileManager.Core.Security;

public static class ExternalPathSyntax
{
    public static bool IsValid(string? path) => path is { Length: >= 3 and <= 4000 } &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && !path.Contains('/') &&
        !path[2..].Contains(':') && !path.Any(char.IsControl) &&
        !path.Split('\\').Any(p => p is "." or ".." || p.Length > 0 && (p.EndsWith('.') || p.EndsWith(' ')));
}
