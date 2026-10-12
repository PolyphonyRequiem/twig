using System.Text;

namespace Twig.Browser;

internal static class BrowserTerminalFrame
{
    internal const string Enter = "\x1b[?7;25;1000;1006;2004s\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1006h\x1b[?2004h\x1b[2J";
    internal const string Leave = "\x1b[?2004l\x1b[?1000l\x1b[?1006l\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l\x1b[?7;25;1000;1006;2004r";

    internal static void Paint(TextWriter output, IReadOnlyList<string> rows, int top, int width, int height, string header, string status)
    {
        width = Math.Max(1, width); height = Math.Max(1, height); top = Math.Max(0, top);
        var frame = new StringBuilder();
        for (var y = 0; y < height; y++)
        {
            frame.Append("\x1b[").Append(y + 1).Append(";1H\x1b[0m\x1b[2K");
            // Footer owns the last visible row even at a one-row viewport.
            if (y == height - 1) frame.Append("\x1b[33m").Append(BrowserTerminalText.Clip(status, width));
            else if (y == 0) frame.Append("\x1b[36m").Append(BrowserTerminalText.Clip(header, width));
            else if ((long)top + y - 1 < rows.Count) frame.Append(BrowserTerminalText.Clip(rows[top + y - 1], width));
        }
        frame.Append("\x1b[0m");
        output.Write(frame.ToString());
        output.Flush();
    }
}
