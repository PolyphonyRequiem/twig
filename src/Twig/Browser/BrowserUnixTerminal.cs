using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Twig.Browser;

/// <summary>Raw POSIX tty transport for linux-x64 and osx-arm64, with byte-exact termios restoration.</summary>
internal sealed partial class BrowserUnixTerminal : IBrowserTerminal
{
    private readonly byte[] _savedTermios = new byte[80];
    private readonly byte[] _bytes = new byte[256];
    // One carried UTF-8 scalar can complete as two UTF-16 chars from the next batch's first byte.
    private readonly char[] _characters = new char[257];
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly BrowserVtInputParser _parser = new();
    private readonly StreamWriter _output;
    private bool _disposed;
    private bool _ended;

    internal unsafe BrowserUnixTerminal()
    {
        if (!((OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) ||
            (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)))
            throw new PlatformNotSupportedException("POSIX browser transport supports linux-x64 and osx-arm64.");
        if (isatty(0) != 1 || isatty(1) != 1) throw new IOException("The browser requires an actual tty, not redirected input/output.");
        var raw = new byte[_savedTermios.Length];
        _output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096, leaveOpen: true);
        fixed (byte* saved = _savedTermios)
        fixed (byte* next = raw)
        {
            if (tcgetattr(0, saved) != 0) throw Error("Cannot capture tty modes.");
            _savedTermios.CopyTo(raw, 0);
            cfmakeraw(next);
            // ABI layouts: Linux x64 cc starts at17, VMIN=6/VTIME=5; Darwin arm64 cc starts at32, VMIN=16/VTIME=17.
            raw[OperatingSystem.IsMacOS() ? 48 : 23] = 0;
            raw[OperatingSystem.IsMacOS() ? 49 : 22] = 0;
            if (tcsetattr(0, 0, next) != 0) throw Error("Cannot enter raw tty mode.");
        }
        try { _output.Write(BrowserTerminalFrame.Enter); _output.Flush(); }
        catch { Dispose(); throw; }
    }

    public (int Width, int Height) Geometry()
    {
        if (ioctl(1, OperatingSystem.IsMacOS() ? 0x40087468ul : 0x5413ul, out var size) != 0)
            throw Error("Cannot read tty geometry.");
        return (Math.Max(1, (int)size.Columns), Math.Max(1, (int)size.Rows));
    }

    public unsafe bool TryRead(out BrowserInput input)
    {
        if (_parser.TryRead(out input)) return true;
        if (_ended) { input = default; return false; }
        var descriptor = new PollDescriptor { FileDescriptor = 0, Events = 1 };
        var ready = poll(ref descriptor, 1, 0);
        if (ready < 0)
        {
            if (Marshal.GetLastPInvokeError() == 4) { input = default; return false; }
            throw Error("Cannot poll tty input.");
        }
        if (ready == 0) { input = default; return false; }
        nint received;
        fixed (byte* buffer = _bytes) received = read(0, buffer, (nuint)_bytes.Length);
        if (received < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 4 or 11 or 35) { input = default; return false; }
            if ((descriptor.ReturnedEvents & 0x18) == 0) throw Error("Cannot read tty input.");
        }
        if (received <= 0)
        {
            _ended = true; _parser.EndInput(); return _parser.TryRead(out input);
        }
        try
        {
            var count = _decoder.GetChars(_bytes.AsSpan(0, (int)received), _characters, flush: false);
            for (var i = 0; i < count; i++) _parser.Feed(_characters[i]);
        }
        catch (DecoderFallbackException ex) { throw new IOException("Terminal input is not valid UTF-8.", ex); }
        return _parser.TryRead(out input);
    }

    public void Paint(IReadOnlyList<string> rows, int top, int width, int height, BrowserChrome chrome)
        => BrowserTerminalFrame.Paint(_output, rows, top, width, height, chrome);

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Write(BrowserTerminalFrame.Leave); _output.Flush(); }
        finally
        {
            fixed (byte* saved = _savedTermios)
            {
                // Always attempt restoration, even after the output side of a pty closes.
                var result = tcsetattr(0, 0, saved);
                _output.Dispose();
                if (result != 0 && isatty(0) == 1) throw Error("Cannot restore inherited tty modes.");
            }
        }
    }

    private static Win32Exception Error(string message) => new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { internal int FileDescriptor; internal short Events; internal short ReturnedEvents; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize { internal ushort Rows, Columns, PixelWidth, PixelHeight; }

    [LibraryImport("libc", SetLastError = true)] private static partial int isatty(int fd);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial int tcgetattr(int fd, byte* modes);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial int tcsetattr(int fd, int action, byte* modes);
    [LibraryImport("libc")] private static unsafe partial void cfmakeraw(byte* modes);
    [LibraryImport("libc", SetLastError = true)] private static partial int ioctl(int fd, ulong request, out WindowSize size);
    [LibraryImport("libc", SetLastError = true)] private static partial int poll(ref PollDescriptor descriptors, nuint count, int milliseconds);
    [LibraryImport("libc", SetLastError = true)] private static unsafe partial nint read(int fd, byte* buffer, nuint count);
}
