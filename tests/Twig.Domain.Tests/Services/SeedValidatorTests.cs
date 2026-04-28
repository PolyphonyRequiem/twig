using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SeedValidatorTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Default rules — only System.Title required, no parent needed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_SeedWithTitle_DefaultRules_Passes()
    {
        var seed = new WorkItemBuilder(-1, "My seed").AsSeed().Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default);

        result.Passed.ShouldBeTrue();
        result.SeedId.ShouldBe(-1);
        result.Title.ShouldBe("My seed");
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SeedWithEmptyTitle_DefaultRules_Fails()
    {
        var seed = new WorkItemBuilder(-2, "").AsSeed().Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("System.Title");
        result.Failures[0].Message.ShouldContain("Title");
    }

    [Fact]
    public void Validate_SeedWithWhitespaceTitle_DefaultRules_Fails()
    {
        var seed = new WorkItemBuilder(-3, "   ").AsSeed().Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("System.Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom required fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_RequiredFieldPresent_Passes()
    {
        var seed = new WorkItemBuilder(-4, "Seed with description")
            .AsSeed()
            .WithField("System.Description", "A description")
            .Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title", "System.Description"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Validate_RequiredFieldMissing_Fails()
    {
        var seed = new WorkItemBuilder(-5, "Seed no description").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title", "System.Description"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("System.Description");
        result.Failures[0].Message.ShouldContain("System.Description");
    }

    [Fact]
    public void Validate_RequiredFieldEmpty_Fails()
    {
        var seed = new WorkItemBuilder(-6, "Seed empty description")
            .AsSeed()
            .WithField("System.Description", "")
            .Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title", "System.Description"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("System.Description");
    }

    [Fact]
    public void Validate_RequiredFieldWhitespace_Fails()
    {
        var seed = new WorkItemBuilder(-7, "Seed whitespace description")
            .AsSeed()
            .WithField("System.Description", "   ")
            .Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title", "System.Description"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("System.Description");
    }

    [Fact]
    public void Validate_MultipleRequiredFieldsMissing_ReportsAll()
    {
        var seed = new WorkItemBuilder(-8, "").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title", "System.Description", "Microsoft.VSTS.Common.Priority"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  RequireParent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_RequireParent_WithParent_Passes()
    {
        var seed = new WorkItemBuilder(-9, "Child seed")
            .AsSeed()
            .WithParent(100)
            .Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title"],
            RequireParent = true,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Validate_RequireParent_WithoutParent_Fails()
    {
        var seed = new WorkItemBuilder(-10, "Orphan seed").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title"],
            RequireParent = true,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].Rule.ShouldBe("RequireParent");
        result.Failures[0].Message.ShouldContain("parent");
    }

    [Fact]
    public void Validate_RequireParentFalse_WithoutParent_Passes()
    {
        var seed = new WorkItemBuilder(-11, "Orphan but OK").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title"],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No required fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_NoRequiredFields_NoParent_Passes()
    {
        var seed = new WorkItemBuilder(-12, "").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = [],
            RequireParent = false,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Combined failures (field + parent)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_MissingFieldAndParent_ReportsBoth()
    {
        var seed = new WorkItemBuilder(-13, "").AsSeed().Build();

        var rules = new SeedPublishRules
        {
            RequiredFields = ["System.Title"],
            RequireParent = true,
        };

        var result = SeedValidator.Validate(seed, rules);

        result.Passed.ShouldBeFalse();
        result.Failures.Count.ShouldBe(2);
    }
}
