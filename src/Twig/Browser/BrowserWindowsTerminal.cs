using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Twig.Browser;

/// <summary>Actual console/ConPTY transport. Native records never escape the normalized terminal seam.</summary>
internal sealed partial class BrowserWindowsTerminal : IBrowserTerminal
{
    private readonly nint _input = GetStdHandle(-10);
    private readonly nint _output = GetStdHandle(-11);
    private readonly uint _savedInput;
    private readonly uint _savedOutput;
    private readonly uint _savedInputCodePage;
    private readonly uint _savedOutputCodePage;
    private readonly Encoding _savedEncoding;
    private readonly BrowserVtInputParser _parser = new();
    private BrowserInput _repeated;
    private int _remaining;
    private bool _disposed;
    private bool _ended;

    internal BrowserWindowsTerminal()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console transport requires Windows.");
        if (!GetConsoleMode(_input, out _savedInput) || !GetConsoleMode(_output, out _savedOutput))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The browser requires an actual console/ConPTY, not redirected input/output.");
        _savedEncoding = Console.OutputEncoding;
        _savedInputCodePage = GetConsoleCP(); _savedOutputCodePage = GetConsoleOutputCP();
        // Raw INPUT_RECORD mode; preserve inherited bits except processed/line/echo/QuickEdit/VT input.
        if (!SetConsoleMode(_input, (_savedInput | 0x98u) & ~0x247u)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetConsoleMode(_output, _savedOutput | 5u))
        { SetConsoleMode(_input, _savedInput); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(BrowserTerminalFrame.Enter);
            Console.Out.Flush();
        }
        catch { Dispose(); throw; }
    }

    public (int Width, int Height) Geometry()
    {
        if (!GetConsoleScreenBufferInfo(_output, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (Math.Max(1, info.Right - info.Left + 1), Math.Max(1, info.Bottom - info.Top + 1));
    }

    public bool TryRead(out BrowserInput input)
    {
        if (_remaining > 0) { _remaining--; input = _repeated; return true; }
        if (_parser.TryRead(out input)) return true;
        if (_ended) { input = default; return false; }
        // Bounded polling keeps application cancellation and geometry responsive.
        for (var count = 0; count < 64; count++)
        {
            var wait = WaitForSingleObject(_input, 0);
            if (wait == 258) return _parser.TryRead(out input);
            if (wait != 0) return End(out input);
            if (!ReadConsoleInputW(_input, out var record, 1, out var read))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 6 or 109 or 232) return End(out input);
                throw new Win32Exception(error);
            }
            if (read == 0) return End(out input);
            var modifiers = Modifiers(record.ControlState);
            if (record.Type == 2)
            {
                if (!GetConsoleScreenBufferInfo(_output, out var info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                input = new BrowserInput(BrowserInputKind.Mouse, Modifiers: Modifiers(record.MouseControlState), X: record.MouseX - info.Left,
                    Y: record.MouseY - info.Top, WheelDelta: (record.MouseFlags & 4) != 0 ? Math.Sign((short)(record.Buttons >> 16)) : 0,
                    ButtonDown: (record.Buttons & 1) != 0 && record.MouseFlags is 0 or 2);
                return true;
            }
            if (record.Type != 1 || record.KeyDown == 0) continue;
            BrowserKey? key = record.VirtualKey switch
            {
                13 => BrowserKey.Enter,
                8 => BrowserKey.Backspace,
                9 => BrowserKey.Tab,
                46 => BrowserKey.Delete,
                38 => BrowserKey.Up,
                40 => BrowserKey.Down,
                37 => BrowserKey.Left,
                39 => BrowserKey.Right,
                33 => BrowserKey.PageUp,
                34 => BrowserKey.PageDown,
                36 => BrowserKey.Home,
                35 => BrowserKey.End,
                _ => null
            };
            // Character records also carry bracketed-paste/VT sequences in ConPTY.
            if (record.Character != 0)
            {
                for (var repeat = 0; repeat < Math.Max(1, (int)record.Repeat); repeat++) _parser.Feed((char)record.Character, modifiers);
                if (_parser.TryRead(out input)) return true;
            }
            else if (key is not null)
            {
                input = new BrowserInput(BrowserInputKind.Key, key.Value, Modifiers: modifiers);
                _repeated = input; _remaining = Math.Max(0, record.Repeat - 1);
                return true;
            }
        }
        return _parser.TryRead(out input);
    }

    private bool End(out BrowserInput input)
    { _ended = true; input = new BrowserInput(BrowserInputKind.EndOfInput); return true; }

    private static BrowserModifiers Modifiers(uint state) =>
        ((state & 0xC) != 0 ? BrowserModifiers.Control : 0) |
        ((state & 3) != 0 ? BrowserModifiers.Alt : 0) |
        ((state & 0x10) != 0 ? BrowserModifiers.Shift : 0);

    public void Paint(IReadOnlyList<string> rows, int top, int width, int height, BrowserChrome chrome)
        => BrowserTerminalFrame.Paint(Console.Out, rows, top, width, height, chrome);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Console.Write(BrowserTerminalFrame.Leave); Console.Out.Flush(); }
        finally
        {
            SetConsoleMode(_input, _savedInput); SetConsoleMode(_output, _savedOutput);
            try { Console.OutputEncoding = _savedEncoding; }
            finally { SetConsoleCP(_savedInputCodePage); SetConsoleOutputCP(_savedOutputCodePage); }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(4)] internal int KeyDown;
        [FieldOffset(8)] internal ushort Repeat;
        [FieldOffset(10)] internal ushort VirtualKey;
        [FieldOffset(14)] internal ushort Character;
        [FieldOffset(16)] internal uint ControlState;
        [FieldOffset(4)] internal short MouseX;
        [FieldOffset(6)] internal short MouseY;
        [FieldOffset(8)] internal uint Buttons;
        [FieldOffset(12)] internal uint MouseControlState;
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
    [LibraryImport("kernel32.dll")] private static partial uint GetConsoleCP();
    [LibraryImport("kernel32.dll")] private static partial uint GetConsoleOutputCP();
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleCP(uint codePage);
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleOutputCP(uint codePage);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleMode(nint handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleMode(nint handle, uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleScreenBufferInfo(nint handle, out BufferInfo info);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReadConsoleInputW(nint handle, out InputRecord record, uint count, out uint read);
}
