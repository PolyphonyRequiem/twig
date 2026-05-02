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

    [Theory]
    [InlineData(60, 28)]
    [InlineData(80, 48)]
    [InlineData(120, 88)]
    [InlineData(200, 168)]
    public void TableTitleBudget_ReturnsWidthMinusOverhead(int width, int expected)
    {
        var budget = new WidthBudget(width);
        budget.TableTitleBudget.ShouldBe(expected);
    }

    // --- PanelContentWidth & GridValueBudget ---

    [Theory]
    [InlineData(80, 74)]
    [InlineData(120, 114)]
    public void PanelContentWidth_ReturnsWidthMinusPanelOverhead(int width, int expected)
    {
        var budget = new WidthBudget(width);
        budget.PanelContentWidth.ShouldBe(expected);
    }

    [Theory]
    [InlineData(80, 60)]
    [InlineData(120, 100)]
    public void GridValueBudget_ReturnsPanelContentMinusLabelWidth(int width, int expected)
    {
        var budget = new WidthBudget(width);
        budget.GridValueBudget.ShouldBe(expected);
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
        var budget = new WidthBudget(80); // Standard
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
        var budget = new WidthBudget(80);
        budget.TreeTitleBudget(0).ShouldBe(80 - WidthBudget.TreeNodeFixedOverhead); // 54
    }

    [Fact]
    public void TreeTitleBudget_ReducesWithDepth()
    {
        var budget = new WidthBudget(80);
        var atZero = budget.TreeTitleBudget(0);
        var atThree = budget.TreeTitleBudget(3);
        atThree.ShouldBeLessThan(atZero);
        atThree.ShouldBe(80 - (3 * WidthBudget.TreeIndentPerLevel) - WidthBudget.TreeNodeFixedOverhead); // 42
    }

    [Fact]
    public void TreeTitleBudget_Width60Depth5_StaysPositive()
    {
        var budget = new WidthBudget(60);
        // 60 - (5*4) - 26 = 14, above the clamp threshold of 10
        budget.TreeTitleBudget(5).ShouldBe(14);
        budget.TreeTitleBudget(5).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void TreeTitleBudget_ClampsToTen_AtExtremeDepth()
    {
        var budget = new WidthBudget(60);
        // depth=100: 60 - (100*4) - 26 = -366 → clamped to 10
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

    [Fact]
    public void TreeTitleBudget_NeverReturnsNegative()
    {
        var budget = new WidthBudget(60);
        for (var depth = 0; depth < 50; depth++)
        {
            budget.TreeTitleBudget(depth).ShouldBeGreaterThanOrEqualTo(10);
        }
    }

    // --- PanelHeaderTitleBudget ---

    [Theory]
    [InlineData(60, 6, 48)]   // PanelContentWidth(60)=54, 54-6=48
    [InlineData(80, 6, 68)]   // PanelContentWidth(80)=74, 74-6=68
    [InlineData(120, 6, 108)] // PanelContentWidth(120)=114, 114-6=108
    public void PanelHeaderTitleBudget_ReturnsPanelContentMinusOverhead(int width, int overhead, int expected)
    {
        var budget = new WidthBudget(width);
        budget.PanelHeaderTitleBudget(overhead).ShouldBe(expected);
    }

    [Fact]
    public void PanelHeaderTitleBudget_ClampsToTen_WhenOverheadExceedsPanelContent()
    {
        var budget = new WidthBudget(60);
        // PanelContentWidth=54, overhead=100 → 54-100 = -46 → clamped to 10
        budget.PanelHeaderTitleBudget(100).ShouldBe(10);
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
