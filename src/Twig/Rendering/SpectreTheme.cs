using Spectre.Console;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Rendering;

/// <summary>
/// Maps type badges, colors, and state styles to Spectre.Console markup and <see cref="Style"/> objects.
/// Instance-based — receives config via constructor injection.
/// </summary>
internal sealed class SpectreTheme
{
    private readonly string _iconMode;
    private readonly Dictionary<string, string>? _typeIconIds;
    private readonly Dictionary<string, string>? _typeColors;
    private readonly Dictionary<string, string>? _appearanceColors;
    private readonly IReadOnlyList<StateEntry>? _stateEntries;

    public SpectreTheme(DisplayConfig displayConfig, List<TypeAppearanceConfig>? typeAppearances = null, IReadOnlyList<StateEntry>? stateEntries = null)
    {
        _iconMode = displayConfig.Icons;
        _typeIconIds = typeAppearances?
            .Where(a => a.IconId is not null)
            .ToDictionary(a => a.Name, a => a.IconId!, StringComparer.OrdinalIgnoreCase);
        _typeColors = displayConfig.TypeColors is null
            ? null
            : new Dictionary<string, string>(displayConfig.TypeColors, StringComparer.OrdinalIgnoreCase);
        _appearanceColors = typeAppearances?
            .Where(a => !string.IsNullOrEmpty(a.Color))
            .ToDictionary(a => a.Name, a => a.Color, StringComparer.OrdinalIgnoreCase);
        _stateEntries = stateEntries;
    }

    // State category → Spectre style
    internal Style GetStateStyle(string state)
    {
        if (string.IsNullOrEmpty(state))
            return new Style(Color.Grey);

        return StateCategoryResolver.Resolve(state, _stateEntries) switch
        {
            StateCategory.Completed or StateCategory.Resolved => new Style(Color.Green),
            StateCategory.InProgress => new Style(Color.Blue),
            StateCategory.Removed => new Style(Color.Red),
            StateCategory.Proposed => new Style(Color.Grey),
            _ => Style.Plain,
        };
    }

    /// <summary>
    /// Formats a state string with Spectre markup matching <c>HumanOutputFormatter</c> colors.
    /// </summary>
    internal string FormatState(string state)
    {
        if (string.IsNullOrEmpty(state))
            return "[grey]—[/]";

        var color = StateCategoryResolver.Resolve(state, _stateEntries) switch
        {
            StateCategory.Completed or StateCategory.Resolved => "green",
            StateCategory.InProgress => "blue",
            StateCategory.Removed => "red",
            StateCategory.Proposed => "grey",
            _ => "default",
        };

        return $"[{color}]{Markup.Escape(state)}[/]";
    }

    /// <summary>
    /// Returns the badge glyph for a work item type via <see cref="IconSet.ResolveTypeBadge"/>.
    /// </summary>
    internal string GetTypeBadge(WorkItemType type)
    {
        return IconSet.ResolveTypeBadge(_iconMode, type.Value, _typeIconIds);
    }

    /// <summary>
    /// Formats a type badge with Spectre markup color.
    /// Uses <see cref="TypeColorResolver"/> for hex colors, falling back to <see cref="DeterministicTypeColor"/>.
    /// </summary>
    internal string FormatTypeBadge(WorkItemType type)
    {
        var badge = GetTypeBadge(type);
        var color = GetSpectreColor(type);
        return $"[{color}]{Markup.Escape(badge)}[/]";
    }

    /// <summary>
    /// Resolves a Spectre markup color string for the given type.
    /// Priority: TypeColorResolver hex → Spectre markup color, fallback: DeterministicTypeColor → Spectre color name.
    /// </summary>
    private string GetSpectreColor(WorkItemType type)
    {
        var hex = TypeColorResolver.ResolveHex(type.Value, _typeColors, _appearanceColors);
        if (hex is not null)
        {
            var markupColor = HexToSpectreColor.ToMarkupColor(hex);
            if (markupColor is not null)
                return markupColor;
        }

        var ansi = DeterministicTypeColor.GetAnsiEscape(type.Value);
        return ansi switch
        {
            "\x1b[35m" => "purple",
            "\x1b[36m" => "aqua",
            "\x1b[34m" => "blue",
            "\x1b[33m" => "yellow",
            "\x1b[32m" => "green",
            "\x1b[31m" => "red",
            _ => "default",
        };
    }

    /// <summary>
    /// Table style for the main workspace table — simple, no borders for a CLI-native feel.
    /// When <paramref name="isTeamView"/> is true, an Assigned column is added.
    /// </summary>
    internal static Table CreateWorkspaceTable(bool isTeamView = false)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]ID[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Type[/]").Centered())
            .AddColumn("[bold]Title[/]")
            .AddColumn("[bold]State[/]");

        if (isTeamView)
            table.AddColumn("[bold]Assigned[/]");

        table.Expand();
        return table;
    }

    /// <summary>
    /// Formats a state category header for use in grouped workspace rendering.
    /// </summary>
    internal static string FormatCategoryHeader(StateCategory category)
    {
        return category switch
        {
            StateCategory.Proposed => "Proposed",
            StateCategory.InProgress => "In Progress",
            StateCategory.Resolved => "Resolved",
            StateCategory.Completed => "Completed",
            _ => category.ToString(),
        };
    }

    /// <summary>
    /// Resolves a work item's state to its <see cref="StateCategory"/>.
    /// </summary>
    internal StateCategory ResolveCategory(string state)
    {
        return StateCategoryResolver.Resolve(state, _stateEntries);
    }
}
