using System.Text;

namespace Twig.Formatters;

/// <summary>Universal messages only; data rendering belongs to RenderTree.</summary>
public sealed class HumanOutputFormatter : IOutputFormatter
{
    public string FormatError(string message) => $"\x1b[31m✗ error:\x1b[0m {message}";
    public string FormatSuccess(string message) => $"\x1b[32m✓\x1b[0m {message}";
    public string FormatHint(string hint) => $"\x1b[33m→\x1b[0m \x1b[2mhint: {hint}\x1b[0m";
    public string FormatInfo(string message) => $"\x1b[2m{message}\x1b[0m";

    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches)
    {
        var buffer = new StringBuilder("Multiple matches:" + Environment.NewLine);
        for (var i = 0; i < matches.Count; i++)
            buffer.AppendLine($"  \x1b[1m[{i + 1}]\x1b[0m #{matches[i].Id} {matches[i].Title}");
        return buffer.ToString().TrimEnd('\r', '\n');
    }
}
