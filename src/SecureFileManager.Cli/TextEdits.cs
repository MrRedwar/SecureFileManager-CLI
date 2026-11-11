using System.Text.RegularExpressions;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Cli;

// Only manipulates the unsaved editor buffer. File validation, ownership and saving stay in Core.
public static class TextEdits
{
    private sealed record Line(string Text, string Ending);
    private static List<Line> Parse(string text)
    {
        var lines = new List<Line>(); var start = 0;
        foreach (Match match in Regex.Matches(text, "\r\n|\r|\n"))
        { lines.Add(new(text[start..match.Index], match.Value)); start = match.Index + match.Length; }
        lines.Add(new(text[start..], "")); return lines;
    }
    public static IReadOnlyList<string> Lines(string text) => Parse(text).Select(x => x.Text).ToArray();
    public static string Check(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > FileRules.TextLimit) throw new ArgumentException("Превышен лимит буфера.");
        return text;
    }
    private static string Join(List<Line> lines) => Check(string.Concat(lines.Select(x => x.Text + x.Ending)));
    private static void SingleLine(string text) { if (text.Contains('\r') || text.Contains('\n')) throw new ArgumentException(); }
    public static string ChangeLine(string text, int index, string value)
    {
        SingleLine(value); var lines = Parse(text);
        if (index < 0 || index >= lines.Count) throw new ArgumentException();
        lines[index] = lines[index] with { Text = value }; return Join(lines);
    }
    public static string InsertLine(string text, int index, string value)
    {
        SingleLine(value); var lines = Parse(text);
        if (index < 0 || index > lines.Count) throw new ArgumentException();
        var ending = lines.FirstOrDefault(x => x.Ending != "")?.Ending ?? "\r\n";
        if (index == lines.Count)
        { lines[^1] = lines[^1] with { Ending = ending }; lines.Add(new(value, "")); }
        else lines.Insert(index, new(value, ending));
        return Join(lines);
    }
    public static string DeleteLine(string text, int index)
    {
        var lines = Parse(text);
        if (index < 0 || index >= lines.Count) throw new ArgumentException();
        lines.RemoveAt(index); return Join(lines);
    }
    public static string Replace(string text, string oldValue, string newValue)
    {
        if (oldValue.Length == 0) throw new ArgumentException();
        long matches = 0;
        for (var at = 0; (at = text.IndexOf(oldValue, at, StringComparison.Ordinal)) >= 0; at += oldValue.Length) matches++;
        // Bound allocation before String.Replace, including a very large expansion of a short fragment.
        if (text.Length + matches * ((long)newValue.Length - oldValue.Length) > FileRules.TextLimit) throw new ArgumentException();
        return Check(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }
}
