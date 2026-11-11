using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SecureFileManager.Cli;

// Console.ReadLine's byte decoding can corrupt non-ASCII input in Windows console hosts.
// Interactive handles use UTF-16 directly; redirected streams retain the configured UTF-8.
public static class UnicodeConsole
{
    public static string? ReadLine(string initial = "")
    {
        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input, out _)) return Console.ReadLine();
        var buffer = new char[Math.Max(4096, initial.Length + 4096)];
        initial.CopyTo(0, buffer, 0, initial.Length);
        var control = new ReadControl { Length = 16, InitialChars = (uint)initial.Length };
        if (initial.Length > 0) Write(initial);
        var result = new StringBuilder();
        while (true)
        {
            if (!ReadConsole(input, buffer, (uint)buffer.Length, out var count, ref control))
                throw new IOException("Не удалось прочитать строку консоли.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            result.Append(buffer, 0, (int)count);
            control.InitialChars = 0;
            if (result[^1] == '\n')
            {
                result.Length--;
                if (result.Length > 0 && result[^1] == '\r') result.Length--;
                return result.ToString();
            }
        }
    }

    public static void Write(string text)
    {
        var output = GetStdHandle(-11);
        if (!GetConsoleMode(output, out _)) { Console.Write(text); return; }
        for (var offset = 0; offset < text.Length;)
        {
            var chunk = text.Substring(offset, Math.Min(16384, text.Length - offset));
            if (!WriteConsole(output, chunk, (uint)chunk.Length, out var written, IntPtr.Zero) || written == 0)
                throw new IOException("Не удалось вывести текст консоли.", new Win32Exception(Marshal.GetLastWin32Error()));
            offset += (int)written;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadControl { public uint Length, InitialChars, WakeupMask, KeyState; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadConsole(IntPtr handle, [In, Out] char[] buffer, uint length, out uint read, ref ReadControl control);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteConsole(IntPtr handle, string text, uint length, out uint written, IntPtr reserved);
}
