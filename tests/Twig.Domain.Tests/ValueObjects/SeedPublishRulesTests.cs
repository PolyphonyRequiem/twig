using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

/// <summary>
/// Tests for the <see cref="SeedPublishRules"/> domain value object.
/// </summary>
public class SeedPublishRulesTests
{
    [Fact]
    public void Default_HasTitleRequiredField()
    {
        var rules = SeedPublishRules.Default;

        rules.RequiredFields.ShouldBe(new[] { "System.Title" });
        rules.RequireParent.ShouldBeFalse();
    }

    [Fact]
    public void Default_ReturnsSameInstance()
    {
        var a = SeedPublishRules.Default;
        var b = SeedPublishRules.Default;

        a.ShouldBeSameAs(b);
    }
}
