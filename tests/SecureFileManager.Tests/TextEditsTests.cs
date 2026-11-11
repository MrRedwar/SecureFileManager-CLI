using SecureFileManager.Cli;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Tests;

public sealed class TextEditsTests
{
    [Fact]
    public void ChangingOneRussianLinePreservesOtherLinesAndMixedEndings()
    {
        const string source = "Первая\r\nЕгипет не тот\nПоследняя\r";
        var edited = TextEdits.ChangeLine(source, 1, "Египет тот самый — ёж");
        Assert.Equal("Первая\r\nЕгипет тот самый — ёж\nПоследняя\r", edited);
        Assert.DoesNotContain('\uFFFD', edited);
        Assert.Equal(edited, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(edited)));
    }
    [Fact]
    public void InsertionDeletionAndReplacementPreserveUntouchedText()
    {
        Assert.Equal("a\nb\nc", TextEdits.InsertLine("a\nc", 1, "b"));
        Assert.Equal("a\nb", TextEdits.InsertLine("a", 1, "b").Replace("\r\n", "\n"));
        Assert.Equal("a\rc", TextEdits.DeleteLine("a\rb\rc", 1));
        Assert.Equal("", TextEdits.DeleteLine("текст", 0));
        Assert.Equal("ёж\nёж", TextEdits.Replace("кот\nкот", "кот", "ёж"));
        Assert.Equal("a\r\n", TextEdits.ChangeLine("a\r\n", 1, ""));
    }
    [Fact]
    public void InvalidEditAndOversizeBufferAreRejected()
    {
        Assert.Throws<ArgumentException>(() => TextEdits.ChangeLine("a", 1, "b"));
        Assert.Throws<ArgumentException>(() => TextEdits.ChangeLine("a", 0, "b\nc"));
        Assert.Throws<ArgumentException>(() => TextEdits.InsertLine("a", -1, "b"));
        Assert.Throws<ArgumentException>(() => TextEdits.Replace("a", "", "b"));
        Assert.Throws<ArgumentException>(() => TextEdits.Replace(new string('a', 100), "a", new string('b', FileRules.TextLimit / 10)));
        Assert.Throws<ArgumentException>(() => TextEdits.ChangeLine("a", 0, new string('я', FileRules.TextLimit / 2 + 1)));
    }
}
