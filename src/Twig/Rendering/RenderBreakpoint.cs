namespace Twig.Rendering;

/// <summary>
/// Defines responsive terminal-width breakpoints for rendering decisions.
/// Breakpoint boundaries (60/80/120) are codified in <c>WidthBudget</c>'s constructor.
/// </summary>
internal enum RenderBreakpoint
{
    /// <summary>60–79 characters.</summary>
    Compact,

    /// <summary>80–119 characters.</summary>
    Standard,

    /// <summary>120+ characters.</summary>
    Wide
}
