using Spectre.Console;
using Twig.Infrastructure.Config;

namespace Twig.Rendering;

internal sealed class SpectreTheme
{
    private readonly string _iconMode;
    private readonly Dictionary<string, string>? _typeIconIds;
    private readonly Dictionary<string, string>? _typeColors;
    private readonly Dictionary<string, string>? _appearanceColors;

    public SpectreTheme(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null)
    {
        _iconMode = displayConfig.ResolveIconMode();
        _typeIconIds = typeAppearances?.Where(a => a.IconId is not null)
            .ToDictionary(a => a.Name, a => a.IconId!, StringComparer.OrdinalIgnoreCase);
        _typeColors = displayConfig.TypeColors is null ? null
            : new(displayConfig.TypeColors, StringComparer.OrdinalIgnoreCase);
        _appearanceColors = typeAppearances?.Where(a => !string.IsNullOrEmpty(a.Color))
            .ToDictionary(a => a.Name, a => a.Color, StringComparer.OrdinalIgnoreCase);
    }

    internal string FormatReviewState(string state) => $"[default]●[/] {Markup.Escape(state)}";

    internal string FormatTypeBadge(string type)
    {
        var badge = IconSet.ResolveTypeBadge(_iconMode, type, _typeIconIds);
        return $"[{GetTypeMarkupColor(type, _typeColors, _appearanceColors)}]{Markup.Escape(badge)}[/]";
    }

    internal static string GetTypeMarkupColor(string typeName, Dictionary<string, string>? typeColors,
        Dictionary<string, string>? appearanceColors)
    {
        var hex = TypeColorResolver.ResolveHex(typeName, typeColors, appearanceColors);
        if (hex is not null && HexToSpectreColor.ToMarkupColor(hex) is { } color) return color;
        return DeterministicTypeColor.GetAnsiEscape(typeName) switch
        {
            "\x1b[35m" => "purple", "\x1b[36m" => "aqua", "\x1b[34m" => "blue",
            "\x1b[33m" => "yellow", "\x1b[32m" => "green", "\x1b[31m" => "red", _ => "default",
        };
    }

    internal static Style OnSurface(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                var s = value / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var bg = Luminance(background);
        bool Readable(Color c)
        {
            var fg = Luminance(c);
            return (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05) >= 4.5;
        }
        if (Readable(foreground)) return new Style(foreground, background);
        for (var step = 1; step <= 255; step++)
            foreach (var end in new[] { 255, 0 })
            {
                byte Mix(byte value) => (byte)(value + (end - value) * step / 255);
                var candidate = new Color(Mix(foreground.R), Mix(foreground.G), Mix(foreground.B));
                if (Readable(candidate)) return new Style(candidate, background);
            }
        return new Style(foreground, background);
    }
}
