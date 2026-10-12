namespace Twig.Browser;

internal interface IBrowserTerminal : IDisposable
{
    (int Width, int Height) Geometry();
    bool TryRead(out BrowserInput input);
    void Paint(IReadOnlyList<string> rows, int top, int width, int height, string header, string status);
}

internal enum BrowserInputKind { Key, Mouse, EndOfInput }
internal enum BrowserKey { Character, Enter, Escape, Backspace, Delete, Tab, Up, Down, Left, Right, PageUp, PageDown, Home, End }

[Flags]
internal enum BrowserModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Paste = 8 }

internal readonly record struct BrowserInput(
    BrowserInputKind Kind,
    BrowserKey Key = BrowserKey.Character,
    char Character = '\0',
    BrowserModifiers Modifiers = BrowserModifiers.None,
    int X = 0,
    int Y = 0,
    int WheelDelta = 0,
    bool ButtonDown = false);

internal static class BrowserTerminal
{
    internal static IBrowserTerminal Open() => OperatingSystem.IsWindows()
        ? new BrowserWindowsTerminal()
        : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? new BrowserUnixTerminal()
            : throw new PlatformNotSupportedException("The native browser supports Windows, Linux and macOS terminals.");
}
