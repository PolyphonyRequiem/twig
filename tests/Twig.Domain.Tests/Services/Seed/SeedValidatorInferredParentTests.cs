using Shouldly;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// twig#260: <c>seed validate</c> must be able to spot a parent that was inherited from the
/// active work item rather than deliberately chosen — the twig#254 case, which twig#256's
/// agreement check could not detect because both stores agreed on the wrong parent.
/// </summary>
/// <remarks>
/// The signal is structural: every path that sets a parent deliberately writes BOTH
/// <c>ParentId</c> and a parent-child link row, while the inference fallback writes only
/// <c>ParentId</c>. These tests pin that contract from the validate side.
/// </remarks>
public class SeedValidatorInferredParentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Validate_ParentIdWithNoLinkRow_Warns()
    {
        // The twig#254 repro: `twig set <item>` then `twig seed new` — parent inherited.
        var seed = new WorkItemBuilder(-1, "Inherited parent").AsSeed().WithParent(42).Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, []);

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].Rule.ShouldBe(SeedParentResolver.InferredParentRuleName);
        result.Warnings[0].Message.ShouldContain("42");
    }

    [Fact]
    public void Validate_ParentIdWithNoLinkRow_StillPasses()
    {
        // A warning must never fail the command — `seed validate` exits 0 (twig#260).
        var seed = new WorkItemBuilder(-1, "Inherited parent").AsSeed().WithParent(42).Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, []);

        result.Passed.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ExplicitlyParentedSeed_DoesNotWarn()
    {
        // `seed new --parent` / `seed link --type parent-child` write both stores.
        var seed = new WorkItemBuilder(-1, "Chosen parent").AsSeed().WithParent(42).Build();
        var links = new[] { new SeedLink(-1, 42, SeedLinkTypes.ParentChild, Now) };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Warnings.ShouldBeEmpty();
        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Validate_UnparentedSeed_DoesNotWarn()
    {
        // twig#258 `--no-parent`: no ParentId at all, so nothing was inferred.
        var seed = new WorkItemBuilder(-1, "No parent").AsSeed().Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, []);

        result.Warnings.ShouldBeEmpty();
        result.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Validate_LinkRowForDifferentSeed_StillWarns()
    {
        // Another seed's parent-child row must not suppress this seed's warning.
        var seed = new WorkItemBuilder(-1, "Inherited parent").AsSeed().WithParent(42).Build();
        var links = new[] { new SeedLink(-2, 42, SeedLinkTypes.ParentChild, Now) };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].Rule.ShouldBe(SeedParentResolver.InferredParentRuleName);
    }

    [Fact]
    public void Validate_NonParentLinkOnly_StillWarns()
    {
        // A related/blocks link says nothing about parentage.
        var seed = new WorkItemBuilder(-1, "Inherited parent").AsSeed().WithParent(42).Build();
        var links = new[] { new SeedLink(-1, 99, SeedLinkTypes.Related, Now) };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].Rule.ShouldBe(SeedParentResolver.InferredParentRuleName);
    }

    [Fact]
    public void Validate_ConflictingParents_FailsWithoutInferredWarning()
    {
        // A link row exists (pointing elsewhere), so this is disagreement — twig#256's
        // failure — not the inferred case. The two findings must not double-report.
        var seed = new WorkItemBuilder(-1, "Conflict").AsSeed().WithParent(42).Build();
        var links = new[] { new SeedLink(-1, 77, SeedLinkTypes.ParentChild, Now) };

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default, links);

        result.Passed.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Rule == SeedParentResolver.RuleName);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullLinks_DoesNotWarn()
    {
        // The links-less overload (publish-time revalidation) has no link table to consult,
        // so it must not manufacture a warning from missing data.
        var seed = new WorkItemBuilder(-1, "Inherited parent").AsSeed().WithParent(42).Build();

        var result = SeedValidator.Validate(seed, SeedPublishRules.Default);

        result.Warnings.ShouldBeEmpty();
    }
}
