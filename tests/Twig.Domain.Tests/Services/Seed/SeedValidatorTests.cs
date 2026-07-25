using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

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

    [Theory]
    [InlineData("System.AreaPath")]
    [InlineData("System.IterationPath")]
    [InlineData("System.AssignedTo")]
    public void Validate_CanonicalFieldDiffersFromField_Fails(string fieldName)
    {
        var builder = new WorkItemBuilder(-7, "Seed")
            .AsSeed()
            .WithField(fieldName, "Edited");

        switch (fieldName)
        {
            case "System.AreaPath":
                builder.WithAreaPath("Canonical");
                break;
            case "System.IterationPath":
                builder.WithIterationPath("Canonical");
                break;
            case "System.AssignedTo":
                builder.AssignedTo("Canonical");
                break;
        }

        var result = SeedValidator.Validate(builder.Build(), SeedPublishRules.Default);

        result.Passed.ShouldBeFalse();
        result.Failures.ShouldContain(failure =>
            failure.Rule == fieldName &&
            failure.Message.Contains("does not match", StringComparison.Ordinal));
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

    // ═══════════════════════════════════════════════════════════════
    //  Parent agreement (twig#254) — presence is not agreement
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_ParentIdConflictsWithLink_Fails()
    {
        // Before twig#254 this passed: validate only checked ParentId presence and never
        // read the link table, so publish was the first thing that could catch it.
        var seed = new WorkItemBuilder(-20, "Child").AsSeed().WithParent(50).Build();
        var links = new[]
        {
            new SeedLink(-20, 100, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Passed.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Rule == SeedParentResolver.RuleName);
    }

    [Fact]
    public void Validate_MultipleParentLinks_Fails()
    {
        var seed = new WorkItemBuilder(-21, "Child").AsSeed().Build();
        var links = new[]
        {
            new SeedLink(-21, 100, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
            new SeedLink(-21, 200, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Passed.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Rule == SeedParentResolver.RuleName);
    }

    [Fact]
    public void Validate_ParentIdAgreesWithLink_Passes()
    {
        var seed = new WorkItemBuilder(-22, "Child").AsSeed().WithParent(100).Build();
        var links = new[]
        {
            new SeedLink(-22, 100, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ParentIdWithNoLinks_Passes()
    {
        // The `seed new` shape: ParentId set, no link row. Agreement is vacuous here —
        // this must NOT become a failure, or every freshly created seed fails validate.
        var seed = new WorkItemBuilder(-23, "Child").AsSeed().WithParent(100).Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, Array.Empty<SeedLink>());

        result.Passed.ShouldBeTrue();
    }
}
