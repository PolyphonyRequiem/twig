using Shouldly;
using Spectre.Console.Testing;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that <see cref="SpectreRenderer.RenderFlowSummaryAsync"/>
/// truncates the title in the success header line at narrow terminal widths.
/// </summary>
public sealed class FlowSummaryWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";

    [Fact]
    public async Task NarrowWidth_ShortTitle_RemainsUntruncated()
    {
        var output = await RenderFlowSummary(60, ShortTitle);

        output.ShouldContain(ShortTitle);
        output.ShouldContain("#42");
    }

    [Fact]
    public async Task NarrowWidth_LongTitle_IsTruncated()
    {
        var output = await RenderFlowSummary(60, LongTitle);

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
        output.ShouldContain("#42");
    }

    [Fact]
    public async Task WideWidth_LongTitle_RemainsUntruncated()
    {
        var output = await RenderFlowSummary(200, LongTitle);

        output.ShouldContain(LongTitle);
        output.ShouldContain("#42");
    }

    [Fact]
    public async Task NarrowWidth_ContainsFlowStartedPrefix()
    {
        var output = await RenderFlowSummary(60, LongTitle);

        output.ShouldContain("Flow started for #42");
    }

    [Fact]
    public async Task NarrowWidth_StateTransitionPresent()
    {
        var output = await RenderFlowSummary(60, ShortTitle);

        output.ShouldContain("New");
        output.ShouldContain("Active");
        output.ShouldContain("→");
    }

    [Fact]
    public async Task NarrowWidth_BranchPresent()
    {
        var output = await RenderFlowSummary(60, ShortTitle, branchName: "feature/42-fix-login");

        output.ShouldContain("feature/42-fix-login");
    }

    [Fact]
    public async Task NarrowWidth_NoLineExceedsWidth()
    {
        var width = 60;
        var output = await RenderFlowSummary(width, LongTitle);

        // The header line (first non-empty line) should not overflow
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerLine = lines.First(l => l.Contains("Flow started"));
        headerLine.TrimEnd().Length.ShouldBeLessThanOrEqualTo(width);
    }

    [Fact]
    public async Task NarrowWidth_LargeId_StillTruncates()
    {
        var output = await RenderFlowSummary(60, LongTitle, itemId: 999999);

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
        output.ShouldContain("#999999");
    }

    private static async Task<string> RenderFlowSummary(
        int width,
        string title,
        string? branchName = null,
        int itemId = 42)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var console = new TestConsole { Profile = { Width = width } };
        var renderer = new SpectreRenderer(console, theme);

        var item = new WorkItemBuilder(itemId, title).AsTask().InState("Active").Build();

        await renderer.RenderFlowSummaryAsync(item, "New", "Active", branchName);
        return console.Output;
    }
}
