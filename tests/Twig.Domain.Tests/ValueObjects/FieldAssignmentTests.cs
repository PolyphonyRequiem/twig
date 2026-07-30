using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

/// <summary>
/// The <c>fieldReferenceName=value</c> argument shape shared by
/// <c>twig new --field</c>, <c>twig seed new --field</c> and <c>twig batch --set</c>.
/// </summary>
public class FieldAssignmentTests
{
    [Fact]
    public void Parse_SplitsKeyAndValue()
    {
        var result = FieldAssignment.ParseAll(["Custom.Mode=AFK"], "--field");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].FieldName.ShouldBe("Custom.Mode");
        result.Value[0].NewValue.ShouldBe("AFK");
    }

    [Fact]
    public void Parse_SplitsOnFirstEqualsOnly()
    {
        var result = FieldAssignment.ParseAll(["Custom.Query=a=b=c"], "--field");

        result.IsSuccess.ShouldBeTrue();
        result.Value[0].NewValue.ShouldBe("a=b=c");
    }

    [Fact]
    public void Parse_EmptyValueIsAllowed()
    {
        var result = FieldAssignment.ParseAll(["Custom.Note="], "--field");

        result.IsSuccess.ShouldBeTrue();
        result.Value[0].NewValue.ShouldBe("");
    }

    [Fact]
    public void Parse_PreservesOrderOfRepeatedPairs()
    {
        // Order is load-bearing: callers apply these as last-writer-wins.
        var result = FieldAssignment.ParseAll(["A=1", "B=2", "A=3"], "--field");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Select(f => f.NewValue).ShouldBe(["1", "2", "3"]);
    }

    [Theory]
    [InlineData("NoEqualsSign")]
    [InlineData("=leadingEquals")]
    public void Parse_MalformedPairFails(string malformed)
    {
        var result = FieldAssignment.ParseAll([malformed], "--field");

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Parse_ErrorNamesTheOffendingFlagAndPair()
    {
        // batch says --set, new/seed say --field; the message must follow the caller
        // rather than hard-coding one flag name into shared code.
        var result = FieldAssignment.ParseAll(["oops"], "--set");

        result.Error.ShouldContain("--set");
        result.Error.ShouldContain("oops");
    }

    [Fact]
    public void Parse_NullOrEmptyInputYieldsEmptyList()
    {
        FieldAssignment.ParseAll(null, "--field").Value.ShouldBeEmpty();
        FieldAssignment.ParseAll([], "--field").Value.ShouldBeEmpty();
    }

    [Fact]
    public void ParseAll_StopsAtFirstMalformedPair()
    {
        // Fail closed: no partial application when any pair is bad.
        var result = FieldAssignment.ParseAll(["Good=1", "bad"], "--field");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("bad");
    }
}
