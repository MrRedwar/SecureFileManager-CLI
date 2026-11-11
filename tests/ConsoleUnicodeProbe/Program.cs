using System.Runtime.InteropServices;
using System.Text;
using SecureFileManager.Cli;

var log = Path.Combine(AppContext.BaseDirectory, "result.txt");
try
{
    var input = Native.GetStdHandle(-10);
    var output = Native.GetStdHandle(-11);
    if (!Native.GetConsoleMode(input, out var mode)) throw new Exception("No private console input: " + Marshal.GetLastWin32Error());
    if (!Native.SetConsoleMode(input, mode | 7)) throw new Exception("mode");
    Native.SetConsoleCP(866); Native.SetConsoleOutputCP(866);
    Native.FlushConsoleInputBuffer(input);
    void Inject(string text)
    {
        var events = text.Select(c => new Native.Event { Type = 1, Down = 1, Repeat = 1, Key = c == '\r' ? (ushort)13 : (ushort)0, Character = c }).ToArray();
        if (!Native.WriteConsoleInput(input, events, (uint)events.Length, out var count) || count != events.Length) throw new Exception("input");
    }
    const string phrase = "Египет не тот — ёж";
    Inject(phrase + "\r");
    var read = UnicodeConsole.ReadLine();
    if (read != phrase) throw new Exception("Unicode read mismatch: " + read);
    var controls = new[] {
        new Native.Event { Type=1, Down=1, Repeat=1, Key=0x24, State=0x100 }, // Home
        new Native.Event { Type=1, Down=1, Repeat=1, Character='Я' },
        new Native.Event { Type=1, Down=1, Repeat=1, Key=0x23, State=0x100 }, // End
        new Native.Event { Type=1, Down=1, Repeat=1, Character='!' },
        new Native.Event { Type=1, Down=1, Repeat=1, Key=13, Character='\r' }
    };
    if (!Native.WriteConsoleInput(input, controls, (uint)controls.Length, out _)) throw new Exception("edit input");
    var edited = UnicodeConsole.ReadLine("Египет");
    if (edited != "ЯЕгипет!") throw new Exception("Prefilled edit mismatch: " + edited);
    UnicodeConsole.Write("Проверка вывода ёж");
    var screen = new StringBuilder(4000);
    if (!Native.ReadConsoleOutputCharacter(output, screen, 4000, 0, out _) || !screen.ToString().Contains("Проверка вывода ёж")) throw new Exception("Unicode output mismatch");
    File.WriteAllText(log, "PASS: ReadConsoleW Russian input, prefilled Home/End editing, WriteConsoleW Russian output; CP866.", new UTF8Encoding(false));
    return 0;
}
catch (Exception e) { File.WriteAllText(log, e.ToString(), new UTF8Encoding(false)); return 1; }

static class Native
{
    [StructLayout(LayoutKind.Explicit, Size=20, CharSet=CharSet.Unicode)]
    public struct Event
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort Key;
        [FieldOffset(12)] public ushort Scan;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint State;
    }
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int value);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleCP(uint cp);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleOutputCP(uint cp);
    [DllImport("kernel32.dll")] public static extern bool FlushConsoleInputBuffer(IntPtr h);
    [DllImport("kernel32.dll", EntryPoint="WriteConsoleInputW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool WriteConsoleInput(IntPtr h, Event[] input, uint count, out uint written);
    [DllImport("kernel32.dll", EntryPoint="ReadConsoleOutputCharacterW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool ReadConsoleOutputCharacter(IntPtr h, StringBuilder output, uint count, uint origin, out uint read);
}
