using Shouldly;
using Twig.Domain.Enums;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class TrackingCleanupPolicyTests
{
    [Fact]
    public void None_HasExpectedValue()
    {
        ((int)TrackingCleanupPolicy.None).ShouldBe(0);
    }

    [Fact]
    public void OnComplete_HasExpectedValue()
    {
        ((int)TrackingCleanupPolicy.OnComplete).ShouldBe(1);
    }

    [Fact]
    public void OnCompleteAndPast_HasExpectedValue()
    {
        ((int)TrackingCleanupPolicy.OnCompleteAndPast).ShouldBe(2);
    }

    [Theory]
    [InlineData("None", TrackingCleanupPolicy.None)]
    [InlineData("OnComplete", TrackingCleanupPolicy.OnComplete)]
    [InlineData("OnCompleteAndPast", TrackingCleanupPolicy.OnCompleteAndPast)]
    public void Parse_KnownValues_Succeeds(string input, TrackingCleanupPolicy expected)
    {
        Enum.Parse<TrackingCleanupPolicy>(input).ShouldBe(expected);
    }

    [Fact]
    public void AllValues_AreDefined()
    {
        var values = Enum.GetValues<TrackingCleanupPolicy>();
        values.Length.ShouldBe(3);
    }
}
