using Shouldly;
using Twig.Domain.Enums;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class TrackingModeTests
{
    [Fact]
    public void Single_HasExpectedValue()
    {
        ((int)TrackingMode.Single).ShouldBe(0);
    }

    [Fact]
    public void Tree_HasExpectedValue()
    {
        ((int)TrackingMode.Tree).ShouldBe(1);
    }

    [Theory]
    [InlineData("Single", TrackingMode.Single)]
    [InlineData("Tree", TrackingMode.Tree)]
    public void Parse_KnownValues_Succeeds(string input, TrackingMode expected)
    {
        Enum.Parse<TrackingMode>(input).ShouldBe(expected);
    }

    [Fact]
    public void Parse_CaseInsensitive_Succeeds()
    {
        Enum.Parse<TrackingMode>("single", ignoreCase: true).ShouldBe(TrackingMode.Single);
        Enum.Parse<TrackingMode>("tree", ignoreCase: true).ShouldBe(TrackingMode.Tree);
    }

    [Fact]
    public void ToString_ReturnsExpected()
    {
        TrackingMode.Single.ToString().ShouldBe("Single");
        TrackingMode.Tree.ToString().ShouldBe("Tree");
    }
}
