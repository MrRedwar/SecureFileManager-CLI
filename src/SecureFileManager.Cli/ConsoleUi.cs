using System.Text;

namespace SecureFileManager.Cli;

public class ConsoleUi
{
    public virtual void Write(string text) => Console.Write(Safe(text));
    public void Line(string text = "") => Write(text + Environment.NewLine);
    public virtual string Read(string prompt)
    {
        Write(prompt + " (/cancel — отмена): ");
        var value = Console.ReadLine() ?? throw new EndOfStreamException();
        if (value == "/cancel") throw new OperationCanceledException();
        return value;
    }
    public virtual string Password(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Для пароля нужен интерактивный терминал. Передача пароля через stdin запрещена.");
        Write(prompt + " (Esc — отмена): ");
        return ReadMasked(() => Console.ReadKey(true), text => Console.Write(text));
    }
    public static string ReadMasked(Func<ConsoleKeyInfo> readKey, Action<string> write)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Escape) { write(Environment.NewLine); throw new OperationCanceledException(); }
            if (key.Key == ConsoleKey.Enter) { write(Environment.NewLine); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) { buffer.Length--; write("\b \b"); }
            else if (!char.IsControl(key.KeyChar) && buffer.Length < 129) { buffer.Append(key.KeyChar); write("*"); }
        }
    }
    public bool Confirm(string text) => Read(text + " [да/нет, по умолчанию нет]").Equals("да", StringComparison.OrdinalIgnoreCase);
    public int Index(int count)
    {
        var text = Read("Номер объекта");
        return int.TryParse(text, out var index) && index >= 1 && index <= count ? index - 1 : throw new ArgumentException("Неверный номер объекта.");
    }
    public void Table(IEnumerable<string[]> rows, params string[] headers)
    {
        Line(string.Join(" | ", headers));
        Line(new string('-', 80));
        foreach (var row in rows) Line(string.Join(" | ", row.Select(x => Cell(x))));
    }
    private static string Cell(string value) { var s = Safe(value).Replace('\r', ' ').Replace('\n', ' '); return s.Length > 70 ? s[..67] + "..." : s; }
    public static string Safe(string value) => new(value.Select(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '�' : c).ToArray());
}
