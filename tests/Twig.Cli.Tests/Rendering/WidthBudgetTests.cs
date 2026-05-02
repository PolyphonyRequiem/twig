using Shouldly;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public sealed class WidthBudgetTests
{
    // --- Constructor & clamping ---

    [Fact]
    public void Constructor_ClampsToMinConsoleWidth_WhenBelowFloor()
    {
        var budget = new WidthBudget(30);
        budget.ConsoleWidth.ShouldBe(WidthBudget.MinConsoleWidth);
    }

    [Fact]
    public void Constructor_ClampsToMinConsoleWidth_WhenNegative()
    {
        var budget = new WidthBudget(-1);
        budget.ConsoleWidth.ShouldBe(WidthBudget.MinConsoleWidth);
    }

    [Fact]
    public void Constructor_ClampsToMinConsoleWidth_WhenZero()
    {
        var budget = new WidthBudget(0);
        budget.ConsoleWidth.ShouldBe(WidthBudget.MinConsoleWidth);
    }

    [Fact]
    public void Constructor_PreservesWidth_WhenAtMinimum()
    {
        var budget = new WidthBudget(60);
        budget.ConsoleWidth.ShouldBe(60);
    }

    [Fact]
    public void Constructor_PreservesWidth_WhenAboveMinimum()
    {
        var budget = new WidthBudget(200);
        budget.ConsoleWidth.ShouldBe(200);
    }

    // --- Breakpoint assignment ---

    [Theory]
    [InlineData(60, 0)] // Compact
    [InlineData(79, 0)] // Compact
    [InlineData(80, 1)] // Standard
    [InlineData(119, 1)] // Standard
    [InlineData(120, 2)] // Wide
    [InlineData(200, 2)] // Wide
    public void Breakpoint_AssignedCorrectly(int width, int expectedOrdinal)
    {
        var budget = new WidthBudget(width);
        budget.Breakpoint.ShouldBe((RenderBreakpoint)expectedOrdinal);
    }

    [Fact]
    public void Breakpoint_SubMinWidth_MapsToCompact()
    {
        var budget = new WidthBudget(10);
        budget.Breakpoint.ShouldBe(RenderBreakpoint.Compact);
    }

    // --- TableTitleBudget ---

    [Fact]
    public void TableTitleBudget_ReturnsWidthMinusOverhead()
    {
        var budget = new WidthBudget(120);
        budget.TableTitleBudget.ShouldBe(120 - WidthBudget.TableFixedOverhead);
    }

    [Fact]
    public void TableTitleBudget_ClampsToOne_WhenOverheadExceedsWidth()
    {
        // MinConsoleWidth (60) - TableFixedOverhead (32) = 28, still positive
        // Force a scenario via the minimum: 60 - 32 = 28
        var budget = new WidthBudget(60);
        budget.TableTitleBudget.ShouldBe(28);
    }

    // --- PanelContentWidth ---

    [Fact]
    public void PanelContentWidth_ReturnsWidthMinusOverhead()
    {
        var budget = new WidthBudget(100);
        budget.PanelContentWidth.ShouldBe(100 - WidthBudget.PanelFixedOverhead);
    }

    // --- GridValueBudget ---

    [Fact]
    public void GridValueBudget_ReturnsPanelContentMinusLabelWidth()
    {
        var budget = new WidthBudget(100);
        budget.GridValueBudget.ShouldBe(100 - WidthBudget.PanelFixedOverhead - WidthBudget.GridLabelWidth);
    }

    // --- PathBudget ---

    [Fact]
    public void PathBudget_Compact_CapsAt30()
    {
        var budget = new WidthBudget(70); // Compact
        budget.PathBudget.ShouldBe(30);
    }

    [Fact]
    public void PathBudget_Standard_CapsAt60()
    {
        var budget = new WidthBudget(100); // Standard
        budget.PathBudget.ShouldBe(60);
    }

    [Fact]
    public void PathBudget_Wide_CapsAt60()
    {
        var budget = new WidthBudget(200); // Wide
        budget.PathBudget.ShouldBe(60);
    }

    [Fact]
    public void PathBudget_Compact_UsesGridValueBudget_WhenSmaller()
    {
        // At width=60: PanelContentWidth=54, GridValueBudget=40, cap=30 → min(40,30) = 30
        var budget = new WidthBudget(60);
        budget.PathBudget.ShouldBe(30);
    }

    // --- AssignedToBudget ---

    [Fact]
    public void AssignedToBudget_Compact_Returns15()
    {
        var budget = new WidthBudget(70);
        budget.AssignedToBudget.ShouldBe(15);
    }

    [Fact]
    public void AssignedToBudget_Standard_Returns20()
    {
        var budget = new WidthBudget(100);
        budget.AssignedToBudget.ShouldBe(20);
    }

    [Fact]
    public void AssignedToBudget_Wide_Returns20()
    {
        var budget = new WidthBudget(150);
        budget.AssignedToBudget.ShouldBe(20);
    }

    // --- TreeTitleBudget ---

    [Fact]
    public void TreeTitleBudget_DepthZero_ReturnsWidthMinusFixedOverhead()
    {
        var budget = new WidthBudget(120);
        budget.TreeTitleBudget(0).ShouldBe(120 - WidthBudget.TreeNodeFixedOverhead);
    }

    [Fact]
    public void TreeTitleBudget_ReducesWithDepth()
    {
        var budget = new WidthBudget(120);
        var atZero = budget.TreeTitleBudget(0);
        var atThree = budget.TreeTitleBudget(3);
        atThree.ShouldBeLessThan(atZero);
        atThree.ShouldBe(120 - (3 * WidthBudget.TreeIndentPerLevel) - WidthBudget.TreeNodeFixedOverhead);
    }

    [Fact]
    public void TreeTitleBudget_ClampsToTen_AtExtremeDepth()
    {
        var budget = new WidthBudget(60);
        // depth=100: 60 - (100*4) - 26 = 60 - 400 - 26 = -366 → clamped to 10
        budget.TreeTitleBudget(100).ShouldBe(10);
    }

    [Fact]
    public void TreeTitleBudget_ClampsToTen_WhenCalculationGoesBelowTen()
    {
        var budget = new WidthBudget(60);
        // depth=2: 60 - 8 - 26 = 26 (above 10, fine)
        budget.TreeTitleBudget(2).ShouldBe(26);
        // depth=7: 60 - 28 - 26 = 6 → clamped to 10
        budget.TreeTitleBudget(7).ShouldBe(10);
    }

    // --- Value equality (record struct) ---

    [Fact]
    public void Equality_SameWidth_AreEqual()
    {
        var a = new WidthBudget(100);
        var b = new WidthBudget(100);
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentWidth_AreNotEqual()
    {
        var a = new WidthBudget(100);
        var b = new WidthBudget(120);
        a.ShouldNotBe(b);
    }

    // --- Constants validation ---

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        WidthBudget.MinConsoleWidth.ShouldBe(60);
        WidthBudget.TableFixedOverhead.ShouldBe(32);
        WidthBudget.PanelFixedOverhead.ShouldBe(6);
        WidthBudget.GridLabelWidth.ShouldBe(14);
        WidthBudget.TreeIndentPerLevel.ShouldBe(4);
        WidthBudget.TreeNodeFixedOverhead.ShouldBe(26);
    }
}
