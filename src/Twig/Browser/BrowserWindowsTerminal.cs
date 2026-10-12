using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Twig.Browser;

/// <summary>
/// Opt-in Windows console/ConPTY transport, adapted from the qualified 1143 slice.
/// This does not select a final cross-platform toolkit or retire supported hosts.
/// </summary>
internal sealed partial class BrowserWindowsTerminal : IDisposable
{
    private readonly nint _input = GetStdHandle(-10);
    private readonly nint _output = GetStdHandle(-11);
    private readonly uint _savedInput;
    private readonly uint _savedOutput;
    private readonly Encoding _savedEncoding;
    private bool _disposed;

    internal BrowserWindowsTerminal()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The opt-in native detail terminal slice is Windows-only; supported Twig browsers are unchanged.");
        if (!GetConsoleMode(_input, out _savedInput) || !GetConsoleMode(_output, out _savedOutput))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Native detail requires an actual console/ConPTY, not redirected input/output.");
        _savedEncoding = Console.OutputEncoding;
        // INPUT_RECORD mode, mouse/window events, no processed Ctrl+C, QuickEdit or VT input.
        if (!SetConsoleMode(_input, (_savedInput | 0x98u) & ~0x247u))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetConsoleMode(_output, _savedOutput | 5u))
        {
            SetConsoleMode(_input, _savedInput);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1006h\x1b[2J");
        }
        catch { Dispose(); throw; }
    }

    internal (int Width, int Height) Geometry()
    {
        if (!GetConsoleScreenBufferInfo(_output, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (Math.Max(1, info.Right - info.Left + 1), Math.Max(1, info.Bottom - info.Top + 1));
    }

    internal bool Read(out InputRecord record)
    {
        record = default;
        var wait = WaitForSingleObject(_input, 16);
        if (wait == 258) return false;
        if (wait != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!ReadConsoleInputW(_input, out record, 1, out var read)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return read == 1;
    }

    internal static void Paint(IReadOnlyList<string> rows, int top, int width, int height, string header, string status)
    {
        var frame = new StringBuilder();
        for (var y = 0; y < height; y++)
        {
            frame.Append("\x1b[").Append(y + 1).Append(";1H\x1b[0m\x1b[2K");
            if (y == 0) frame.Append("\x1b[36m").Append(BrowserTerminalText.Clip(header, width));
            else if (y == height - 1) frame.Append("\x1b[33m").Append(BrowserTerminalText.Clip(status, width));
            else if (top + y - 1 < rows.Count) frame.Append(BrowserTerminalText.Clip(rows[top + y - 1], width));
        }
        frame.Append("\x1b[0m");
        Console.Write(frame.ToString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Console.Write("\x1b[?1000l\x1b[?1006l\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l"); }
        finally
        {
            SetConsoleMode(_input, _savedInput);
            SetConsoleMode(_output, _savedOutput);
            Console.OutputEncoding = _savedEncoding;
        }
    }

    // Win32 INPUT_RECORD is 20 bytes: WORD type, 2-byte padding, 16-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort Repeat;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(14)] internal ushort Character;
        [FieldOffset(16)] internal uint KeyModifiers;
        [FieldOffset(8)] internal uint Buttons;
        [FieldOffset(16)] internal uint MouseFlags;
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
