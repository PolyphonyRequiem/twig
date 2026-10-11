using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Twig.TuiAotPrototype1143;

// THROWAWAY: Windows-only input seam; not the production terminal-toolkit decision.
internal sealed partial class WindowsTerminal : IDisposable
{
    private readonly nint input = GetStdHandle(-10);
    private readonly nint output = GetStdHandle(-11);
    private readonly uint savedInput;
    private readonly uint savedOutput;
    private readonly Encoding savedEncoding;
    private bool disposed;

    internal WindowsTerminal()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This fixture input seam is Windows-only.");
        if (!GetConsoleMode(input, out savedInput) || !GetConsoleMode(output, out savedOutput))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Run panel in an actual console/ConPTY, not redirected stdin/stdout.");
        savedEncoding = Console.OutputEncoding;
        // INPUT_RECORD mode, mouse/window input, no processed Ctrl+C, no QuickEdit/VT input.
        if (!SetConsoleMode(input, (savedInput | 0x98u) & ~0x247u))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetConsoleMode(output, savedOutput | 5u))
        {
            SetConsoleMode(input, savedInput);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1006h\x1b[2J");
        }
        catch { Dispose(); throw; }
    }

    internal (int Width, int Height, int Left, int Top) Geometry()
    {
        if (!GetConsoleScreenBufferInfo(output, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (Math.Max(1, info.Right - info.Left + 1), Math.Max(1, info.Bottom - info.Top + 1), info.Left, info.Top);
    }

    internal bool Read(out InputRecord record)
    {
        record = default;
        var wait = WaitForSingleObject(input, 16);
        if (wait == 258) return false;
        if (wait != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!ReadConsoleInputW(input, out record, 1, out var read)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return read == 1;
    }

    internal static void Paint(IReadOnlyList<ScreenLine> rows, int top, int width, int height, int selected, string header, string status)
    {
        var frame = new StringBuilder();
        for (var y = 0; y < height; y++)
        {
            frame.Append("\x1b[").Append(y + 1).Append(";1H\x1b[0m\x1b[2K");
            if (y == 0) frame.Append("\x1b[36m").Append(CellText.Clip(header, width));
            else if (y == height - 1) frame.Append("\x1b[33m").Append(CellText.Clip(status, width));
            else if (top + y - 1 < rows.Count)
            {
                var row = rows[top + y - 1];
                if (row.Bench && row.ItemId == selected) frame.Append("\x1b[7m");
                frame.Append(CellText.Clip(row.Text, width));
            }
        }
        frame.Append("\x1b[0m");
        Console.Write(frame.ToString());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Console.Write("\x1b[?1000l\x1b[?1006l\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l"); }
        finally
        {
            SetConsoleMode(input, savedInput);
            SetConsoleMode(output, savedOutput);
            Console.OutputEncoding = savedEncoding;
        }
    }

    // Win32 INPUT_RECORD is 20 bytes: WORD type, 2-byte padding, 16-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint KeyModifiers;
        [FieldOffset(4)] public short MouseX;
        [FieldOffset(6)] public short MouseY;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(12)] public uint MouseModifiers;
        [FieldOffset(16)] public uint MouseFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public short SizeX, SizeY, CursorX, CursorY;
        public ushort Attributes;
        public short Left, Top, Right, Bottom, MaxX, MaxY;
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int handle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleMode(nint handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleMode(nint handle, uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleScreenBufferInfo(nint handle, out BufferInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReadConsoleInputW(nint handle, out InputRecord record, uint count, out uint read);
}
