namespace Twig.Rendering;

/// <summary>
/// Pure value object that centralizes all width-budget calculations for a given
/// terminal width. Created per-render — terminal width can change between renders
/// and struct creation cost is negligible.
/// </summary>
internal readonly record struct WidthBudget
{
    /// <summary>Absolute floor for console width.</summary>
    public const int MinConsoleWidth = 60;

    /// <summary>ID(6) + Type(4) + State(10) + borders/padding(~12).</summary>
    public const int TableFixedOverhead = 32;

    /// <summary>Panel border + padding.</summary>
    public const int PanelFixedOverhead = 6;

    /// <summary>Label column width (e.g., "[dim]Area:[/]").</summary>
    public const int GridLabelWidth = 14;

    /// <summary>Spectre.Console tree indentation per depth level.</summary>
    public const int TreeIndentPerLevel = 4;

    /// <summary>badge(3) + ID(7) + state(12) + spaces(4).</summary>
    public const int TreeNodeFixedOverhead = 26;

    /// <summary>Console width clamped to <see cref="MinConsoleWidth"/> minimum.</summary>
    public int ConsoleWidth { get; }

    /// <summary>Responsive breakpoint computed from <see cref="ConsoleWidth"/>.</summary>
    public RenderBreakpoint Breakpoint { get; }

    /// <summary>Available width for title text in table rows.</summary>
    public int TableTitleBudget => Math.Max(ConsoleWidth - TableFixedOverhead, 1);

    /// <summary>Available content width inside a panel.</summary>
    public int PanelContentWidth => Math.Max(ConsoleWidth - PanelFixedOverhead, 1);

    /// <summary>Available width for grid value columns inside a panel.</summary>
    public int GridValueBudget => Math.Max(PanelContentWidth - GridLabelWidth, 1);

    /// <summary>Budget for area/iteration path display.</summary>
    public int PathBudget => Math.Min(GridValueBudget, Breakpoint == RenderBreakpoint.Compact ? 30 : 60);

    /// <summary>Budget for assigned-to display name.</summary>
    public int AssignedToBudget => Breakpoint == RenderBreakpoint.Compact ? 15 : 20;

    public WidthBudget(int consoleWidth)
    {
        ConsoleWidth = Math.Max(consoleWidth, MinConsoleWidth);
        Breakpoint = ConsoleWidth switch
        {
            < 80 => RenderBreakpoint.Compact,
            < 120 => RenderBreakpoint.Standard,
            _ => RenderBreakpoint.Wide
        };
    }

    /// <summary>
    /// Available width for title text in tree nodes at the given <paramref name="depth"/>.
    /// Clamped to a minimum of 10 to avoid negative/zero budgets at deep nesting.
    /// </summary>
    public int TreeTitleBudget(int depth) =>
        Math.Max(ConsoleWidth - (depth * TreeIndentPerLevel) - TreeNodeFixedOverhead, 10);
}
