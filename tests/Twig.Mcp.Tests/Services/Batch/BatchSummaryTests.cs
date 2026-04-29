using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class BatchSummaryTests
{
    [Fact]
    public void FromSteps_EmptyList_ReturnsAllZeros()
    {
        var summary = BatchSummary.FromSteps([]);

        summary.Total.ShouldBe(0);
        summary.Succeeded.ShouldBe(0);
        summary.Failed.ShouldBe(0);
        summary.Skipped.ShouldBe(0);
    }

    [Fact]
    public void FromSteps_AllSucceeded_CountsCorrectly()
    {
        var steps = new[]
        {
            Succeeded(0, """{"ok":true}"""),
            Succeeded(1, """{"ok":true}"""),
            Succeeded(2, """{"ok":true}""")
        };

        var summary = BatchSummary.FromSteps(steps);

        summary.Total.ShouldBe(3);
        summary.Succeeded.ShouldBe(3);
        summary.Failed.ShouldBe(0);
        summary.Skipped.ShouldBe(0);
    }

    [Fact]
    public void FromSteps_AllFailed_CountsCorrectly()
    {
        var steps = new[] { Failed(0), Failed(1) };

        var summary = BatchSummary.FromSteps(steps);

        summary.Total.ShouldBe(2);
        summary.Succeeded.ShouldBe(0);
        summary.Failed.ShouldBe(2);
        summary.Skipped.ShouldBe(0);
    }

    [Fact]
    public void FromSteps_AllSkipped_CountsCorrectly()
    {
        var steps = new[] { Skipped(0), Skipped(1), Skipped(2) };

        var summary = BatchSummary.FromSteps(steps);

        summary.Total.ShouldBe(3);
        summary.Succeeded.ShouldBe(0);
        summary.Failed.ShouldBe(0);
        summary.Skipped.ShouldBe(3);
    }

    [Fact]
    public void FromSteps_MixedStatuses_CountsCorrectly()
    {
        var steps = new[]
        {
            Succeeded(0, """{"ok":true}"""),
            Failed(1),
            Skipped(2),
            Succeeded(3, """{"ok":true}"""),
            Skipped(4)
        };

        var summary = BatchSummary.FromSteps(steps);

        summary.Total.ShouldBe(5);
        summary.Succeeded.ShouldBe(2);
        summary.Failed.ShouldBe(1);
        summary.Skipped.ShouldBe(2);
    }

    [Fact]
    public void FromSteps_SingleStep_Succeeded()
    {
        var summary = BatchSummary.FromSteps([Succeeded(0, """{"ok":true}""")]);

        summary.Total.ShouldBe(1);
        summary.Succeeded.ShouldBe(1);
        summary.Failed.ShouldBe(0);
        summary.Skipped.ShouldBe(0);
    }

    [Fact]
    public void FromSteps_SingleStep_Failed()
    {
        var summary = BatchSummary.FromSteps([Failed(0)]);

        summary.Total.ShouldBe(1);
        summary.Succeeded.ShouldBe(0);
        summary.Failed.ShouldBe(1);
        summary.Skipped.ShouldBe(0);
    }

    [Fact]
    public void FromSteps_SingleStep_Skipped()
    {
        var summary = BatchSummary.FromSteps([Skipped(0)]);

        summary.Total.ShouldBe(1);
        summary.Succeeded.ShouldBe(0);
        summary.Failed.ShouldBe(0);
        summary.Skipped.ShouldBe(1);
    }

    [Fact]
    public void FromSteps_TotalEqualsSum()
    {
        var steps = new[]
        {
            Succeeded(0, """{"ok":true}"""),
            Failed(1),
            Skipped(2),
            Succeeded(3, """{"ok":true}"""),
            Failed(4),
            Skipped(5),
            Succeeded(6, """{"ok":true}""")
        };

        var summary = BatchSummary.FromSteps(steps);

        summary.Total.ShouldBe(summary.Succeeded + summary.Failed + summary.Skipped);
    }
}
