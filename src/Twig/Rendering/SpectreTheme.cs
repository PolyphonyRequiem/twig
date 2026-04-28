using Spectre.Console;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Process;
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
    /// Returns the Spectre markup color string for a <see cref="StateCategory"/>
    /// (e.g. "green", "blue", "grey", "red"). Single source of truth for state→color mapping.
    /// </summary>
    internal static string GetCategoryMarkupColor(StateCategory category)
    {
        return category switch
        {
            StateCategory.Completed or StateCategory.Resolved => "green",
            StateCategory.InProgress => "blue",
            StateCategory.Removed => "red",
            StateCategory.Proposed => "grey",
            _ => "default",
        };
    }

    /// <summary>
    /// Returns the Spectre markup color string for a given state (e.g. "green", "blue", "grey", "red").
    /// Resolves the state to a <see cref="StateCategory"/> and delegates to <see cref="GetCategoryMarkupColor"/>.
    /// </summary>
    internal string GetStateCategoryMarkupColor(string state)
    {
        if (string.IsNullOrEmpty(state))
            return "grey";

        return GetCategoryMarkupColor(StateCategoryResolver.Resolve(state, _stateEntries));
    }

    /// <summary>
    /// Formats a state string with Spectre markup matching <c>HumanOutputFormatter</c> colors.
    /// Delegates to <see cref="GetStateCategoryMarkupColor"/> for the color.
    /// </summary>
    internal string FormatState(string state)
    {
        if (string.IsNullOrEmpty(state))
            return "[grey]—[/]";

        var color = GetStateCategoryMarkupColor(state);
        return $"[[[{color}]{Markup.Escape(state)}[/]]]";
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
        var color = GetTypeMarkupColor(type.Value, _typeColors, _appearanceColors);
        return $"[{color}]{Markup.Escape(badge)}[/]";
    }

    /// <summary>
    /// Resolves a Spectre markup color string for the given type name.
    /// Priority: TypeColorResolver hex → Spectre markup color, fallback: DeterministicTypeColor → Spectre color name.
    /// </summary>
    internal static string GetTypeMarkupColor(string typeName, Dictionary<string, string>? typeColors, Dictionary<string, string>? appearanceColors)
    {
        var hex = TypeColorResolver.ResolveHex(typeName, typeColors, appearanceColors);
        if (hex is not null)
        {
            var markupColor = HexToSpectreColor.ToMarkupColor(hex);
            if (markupColor is not null)
                return markupColor;
        }

        return DeterministicTypeColor.GetAnsiEscape(typeName) switch
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
    /// <paramref name="dynamicColumns"/> adds extra data-driven columns after the core 4.
    /// </summary>
    internal static Table CreateWorkspaceTable(bool isTeamView = false, IReadOnlyList<Domain.ValueObjects.ColumnSpec>? dynamicColumns = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]ID[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Type[/]").Centered())
            .AddColumn("[bold]Title[/]")
            .AddColumn(new TableColumn("[bold]State[/]").RightAligned());

        if (isTeamView)
            table.AddColumn("[bold]Assigned[/]");

        if (dynamicColumns is not null)
        {
            foreach (var col in dynamicColumns)
            {
                var alignment = col.DataType.ToLowerInvariant() is "integer" or "double"
                    ? new TableColumn($"[bold]{Markup.Escape(col.DisplayName)}[/]").RightAligned()
                    : new TableColumn($"[bold]{Markup.Escape(col.DisplayName)}[/]");
                table.AddColumn(alignment);
            }
        }

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

    /// <summary>
    /// Returns a Spectre-markup seed indicator based on the configured icon mode.
    /// Unicode mode: green ●, Nerd Font mode: green  (seedling).
    /// </summary>
    internal string FormatSeedIndicator()
    {
        var glyph = _iconMode == "nerd" ? "\uf4d8" : "●";
        return $"[green]{glyph}[/]";
    }
}
