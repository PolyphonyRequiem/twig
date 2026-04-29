using Shouldly;
using Twig.Domain.Services.Seed;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

public sealed class SeedIdEscapeValidatorTests
{
    private static readonly HashSet<int> SeedIds = new() { -1, -2, -3, -4, -5 };

    // ═══════════════════════════════════════════════════════════════
    //  No leaks
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_SeedWithNoFields_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean seed").AsSeed().Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_FieldWithNonSeedNegativeNumber_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean seed")
            .AsSeed()
            .WithField("System.Description", "Temperature is -999 degrees")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullFieldValues_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean seed")
            .AsSeed()
            .WithField("System.Description", null)
            .WithField("Custom.Notes", null)
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_FieldWithPositiveNumber_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean seed")
            .AsSeed()
            .WithField("System.Description", "Linked to item 42")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single field leak
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_FieldContainsSeedId_ReturnsFailure()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "-3")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(1);
        result[0].Rule.ShouldBe("System.Description");
        result[0].Message.ShouldContain("-3");
        result[0].Message.ShouldContain("sentinel");
    }

    [Fact]
    public void Validate_FieldContainsSeedIdNotInSet_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "-3")
            .Build();

        // Only -1 and -2 in the set, not -3
        var limitedIds = new HashSet<int> { -1, -2 };

        var result = SeedIdEscapeValidator.Validate(seed, limitedIds);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SeedIdEmbeddedInText_ReturnsFailure()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "depends on -3 for completion")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(1);
        result[0].Rule.ShouldBe("System.Description");
        result[0].Message.ShouldContain("-3");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple leaking fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_MultipleLeakingFields_ReturnsMultipleFailures()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "see -3")
            .WithField("Custom.Notes", "blocked by -4")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(2);
        result.ShouldContain(f => f.Rule == "System.Description");
        result.ShouldContain(f => f.Rule == "Custom.Notes");
    }

    [Fact]
    public void Validate_SingleFieldMultipleSeedIds_ReturnsMultipleFailures()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "depends on -3 and -5")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(2);
        result.ShouldContain(f => f.Message.Contains("-3"));
        result.ShouldContain(f => f.Message.Contains("-5"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Title checks
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_TitleContainsSeedId_ReturnsFailure()
    {
        var seed = new WorkItemBuilder(-1, "Fix issue -3")
            .AsSeed()
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(1);
        result[0].Rule.ShouldBe("System.Title");
        result[0].Message.ShouldContain("-3");
    }

    [Fact]
    public void Validate_TitleContainsOwnSeedId_ReturnsFailure()
    {
        var seed = new WorkItemBuilder(-1, "Seed -1 description")
            .AsSeed()
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.Count.ShouldBe(1);
        result[0].Rule.ShouldBe("System.Title");
        result[0].Message.ShouldContain("-1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_EmptySeedIdSet_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "-3")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_FieldValueWithOnlyText_ReturnsEmpty()
    {
        var seed = new WorkItemBuilder(-1, "Clean title")
            .AsSeed()
            .WithField("System.Description", "no numbers here")
            .Build();

        var result = SeedIdEscapeValidator.Validate(seed, SeedIds);

        result.ShouldBeEmpty();
    }
}
