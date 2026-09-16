using Shouldly;
using Twig.Domain.Services.ChangeProposals;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class ReviewTextChangeTests
{
    [Theory]
    [InlineData("same", "same", 0, 0)]
    [InlineData("", "new", 0, 3)]
    [InlineData("old", "", 3, 0)]
    [InlineData("A😀Z", "A😁Z", 1, 1)]
    [InlineData("A😀Z", "AXZ", 1, 1)]
    [InlineData("abcXdefYghi", "abcQdefRghi", 5, 5)]
    [InlineData("é", "é", 2, 1)]
    public void MetricCountsUnequalUnicodeScalarSpans_NotMinimalEdits(string before, string after, int removed, int inserted)
    {
        var metric = ReviewTextChange.Measure(before, after);
        metric.Removed.ShouldBe(removed);
        metric.Inserted.ShouldBe(inserted);
        metric.Unit.ShouldBe("unicode-scalars");
        metric.Metric.ShouldBe("common-affix-replacement-v1");
    }

    [Fact]
    public void ArbitrarilyLargeBodiesUseLinearScan_NotQuadraticDiff()
    {
        var prefix = new string('a', 1_000_000);
        var metric = ReviewTextChange.Measure(prefix + "😀" + prefix, prefix + "different" + prefix);
        metric.Removed.ShouldBe(1);
        metric.Inserted.ShouldBe(9);
    }
}
