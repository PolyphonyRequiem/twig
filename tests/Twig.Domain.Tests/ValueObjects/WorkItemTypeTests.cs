using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class WorkItemTypeTests
{
    [Theory]
    [InlineData("Epic")]
    [InlineData("Feature")]
    [InlineData("User Story")]
    [InlineData("Product Backlog Item")]
    [InlineData("Requirement")]
    [InlineData("Task")]
    [InlineData("Bug")]
    [InlineData("Issue")]
    [InlineData("Test Case")]
    [InlineData("Impediment")]
    [InlineData("Change Request")]
    [InlineData("Review")]
    [InlineData("Risk")]
    public void Parse_KnownType_ReturnsSuccess(string typeName)
    {
        var result = WorkItemType.Parse(typeName);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(typeName);
    }

    [Theory]
    [InlineData("epic", "Epic")]
    [InlineData("FEATURE", "Feature")]
    [InlineData("user story", "User Story")]
    [InlineData("BUG", "Bug")]
    [InlineData("task", "Task")]
    public void Parse_CaseInsensitive(string input, string expected)
    {
        var result = WorkItemType.Parse(input);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(expected);
    }

    [Fact]
    public void Parse_UnknownType_ReturnsSuccess()
    {
        var result = WorkItemType.Parse("Unknown Widget");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("Unknown Widget");
    }

    [Fact]
    public void Parse_CustomType_ReturnsSuccess()
    {
        var result = WorkItemType.Parse("Scenario");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("Scenario");
    }

    [Fact]
    public void Parse_CustomType_PreservesCasing()
    {
        var result = WorkItemType.Parse("My Custom Type");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("My Custom Type");
    }

    [Fact]
    public void Equality_DifferentCasing_CustomType()
    {
        var a = WorkItemType.Parse("Scenario").Value;
        var b = WorkItemType.Parse("scenario").Value;
        // Custom types preserve casing, so different casing = different values
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Parse_Empty_ReturnsFail()
    {
        var result = WorkItemType.Parse("");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_Null_ReturnsFail()
    {
        var result = WorkItemType.Parse(null!);
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Parse_Whitespace_ReturnsFail()
    {
        var result = WorkItemType.Parse("   ");
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Parse_TrimsPadding()
    {
        var result = WorkItemType.Parse("  Bug  ");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("Bug");
    }

    [Fact]
    public void Equality_SameParsed()
    {
        var a = WorkItemType.Parse("Bug").Value;
        var b = WorkItemType.Parse("bug").Value;
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_ParsedAndConstant()
    {
        var parsed = WorkItemType.Parse("Bug").Value;
        parsed.ShouldBe(WorkItemType.Bug);
    }

    [Fact]
    public void Inequality_DifferentTypes()
    {
        WorkItemType.Bug.ShouldNotBe(WorkItemType.Task);
    }
}
