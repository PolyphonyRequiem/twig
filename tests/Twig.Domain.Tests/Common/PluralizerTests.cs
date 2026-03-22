using Shouldly;
using Twig.Domain.Common;
using Xunit;

namespace Twig.Domain.Tests.Common;

public class PluralizerTests
{
    [Theory]
    [InlineData("Epic", "Epics")]
    [InlineData("Feature", "Features")]
    [InlineData("Task", "Tasks")]
    [InlineData("Bug", "Bugs")]
    public void Pluralize_StandardTypes_AppendsS(string input, string expected)
    {
        Pluralizer.Pluralize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Story", "Stories")]
    [InlineData("Category", "Categories")]
    [InlineData("User Story", "User Stories")]
    public void Pluralize_ConsonantPlusY_ReplacesWithIes(string input, string expected)
    {
        Pluralizer.Pluralize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Key", "Keys")]       // vowel + y → just "s"
    [InlineData("Day", "Days")]       // vowel + y → just "s"
    public void Pluralize_VowelPlusY_AppendsS(string input, string expected)
    {
        Pluralizer.Pluralize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Process", "Processes")]
    [InlineData("Tax", "Taxes")]
    public void Pluralize_SibilantEndings_AppendsEs(string input, string expected)
    {
        Pluralizer.Pluralize(input).ShouldBe(expected);
    }

    [Fact]
    public void Pluralize_EmptyString_ReturnsEmpty()
    {
        Pluralizer.Pluralize("").ShouldBe("");
    }

    [Fact]
    public void Pluralize_Null_ReturnsNull()
    {
        Pluralizer.Pluralize(null).ShouldBeNull();
    }

    [Fact]
    public void Pluralize_SingleCharacter_AppendsS()
    {
        Pluralizer.Pluralize("A").ShouldBe("As");
    }
}
