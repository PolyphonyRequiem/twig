using System.Text;

namespace Twig.Browser;

internal sealed record BrowserChrome(string Header, string Context, string Notice, string Footer, bool Configuration = false);

internal static class BrowserTerminalFrame
{
    internal const string Enter = "\x1b[?7;25;1000;1006;2004s\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1006h\x1b[?2004h\x1b[2J";
    internal const string Leave = "\x1b[?2004l\x1b[?1000l\x1b[?1006l\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l\x1b[?7;25;1000;1006;2004r";
    private static readonly bool ColorsEnabled = Environment.GetEnvironmentVariable("NO_COLOR") is null &&
        !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal);
    internal static readonly Spectre.Console.Color ContentBackground = new(15, 23, 34);
    private static readonly string ContentStyle = $"\x1b[48;2;{ContentBackground.R};{ContentBackground.G};{ContentBackground.B}m\x1b[38;2;177;217;239m";

    internal static int ContentStart(int height) => height >= 5 ? 3 : height >= 4 ? 2 : height >= 2 ? 1 : 0;
    internal static int ContentHeight(int height) => Math.Max(0, height - ContentStart(height) - (height >= 5 ? 2 : 1));

    internal static void Paint(TextWriter output, IReadOnlyList<string> rows, int top, int width, int height, BrowserChrome chrome)
    {
        width = Math.Max(1, width); height = Math.Max(1, height); top = Math.Max(0, top);
        var configuration = chrome.Configuration;
        var bar = configuration ? "\x1b[48;2;57;35;65m\x1b[38;2;245;227;248m" : "\x1b[48;2;22;40;59m\x1b[38;2;177;217;239m";
        var divider = configuration ? "\x1b[48;2;57;35;65m\x1b[38;2;229;163;222m" : "\x1b[48;2;22;40;59m\x1b[38;2;57;84;106m";
        var footer = configuration ? bar : "\x1b[48;2;27;37;52m\x1b[38;2;152;175;195m";
        var body = ContentStyle;
        var start = ContentStart(height);
        var contentHeight = ContentHeight(height);
        var frame = new StringBuilder();
        for (var y = 0; y < height; y++)
        {
            frame.Append("\x1b[").Append(y + 1).Append(";1H\x1b[0m");
            string text, style;
            if (y == height - 1) { text = chrome.Footer; style = footer; }
            else if (y == 0) { text = chrome.Header; style = bar; }
            else if (y == 1 && height >= 4) { text = chrome.Context; style = bar; }
            else if (y == 2 && height >= 5) { text = new string('─', width); style = divider; }
            else if (height >= 5 && y == height - 2)
            { text = chrome.Notice.Length == 0 ? new string('─', width) : chrome.Notice; style = chrome.Notice.Length == 0 ? divider : "\x1b[48;2;27;37;52m\x1b[38;2;255;190;105m"; }
            else
            {
                var index = (long)top + y - start;
                text = y >= start && y < start + contentHeight && index >= 0 && index < rows.Count ? rows[(int)index] : "";
                style = body;
            }
            if (!ColorsEnabled) { text = BrowserTerminalText.PlainText(text); style = ""; }
            frame.Append(style).Append("\x1b[2K");
            var clipped = BrowserTerminalText.Clip(text, width);
            frame.Append(clipped).Append(style).Append(' ', Math.Max(0, width - BrowserTerminalText.CellWidth(clipped)));
        }
        frame.Append("\x1b[0m");
        output.Write(frame.ToString());
        output.Flush();
    }
}
