using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedLinkCommandTests
{
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly SeedLinkCommand _cmd;

    public SeedLinkCommandTests()
    {
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();

        // Default: no seeds and no links — cycle detection passes through safely
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());

        _cmd = new SeedLinkCommand(_seedLinkRepo, _workItemRepo, formatterFactory);
    }

    // ── LinkAsync tests ─────────────────────────────────────────────

    [Fact]
    public async Task Link_SeedToSeed_DefaultType_CreatesRelatedLink()
    {
        var result = await _cmd.LinkAsync(-1, -2, null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.SourceId == -1 && l.TargetId == -2 && l.LinkType == SeedLinkTypes.Related),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_SeedToPositive_WithType_CreatesBlocksLink()
    {
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _cmd.LinkAsync(-1, 12345, "blocks");

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.SourceId == -1 && l.TargetId == 12345 && l.LinkType == SeedLinkTypes.Blocks),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_PositiveToPositive_Rejected()
    {
        var result = await _cmd.LinkAsync(100, 200, null);

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_InvalidType_Rejected()
    {
        var result = await _cmd.LinkAsync(-1, -2, "invalid");

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_TypeIsCaseInsensitive()
    {
        var result = await _cmd.LinkAsync(-1, -2, "BLOCKS");

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.LinkType == SeedLinkTypes.Blocks),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_DuplicateLink_ReportsError()
    {
        _seedLinkRepo.AddLinkAsync(Arg.Any<SeedLink>(), Arg.Any<CancellationToken>())
            .Throws(new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed", 19));

        var result = await _cmd.LinkAsync(-1, -2, null);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Link_PositiveIdNotCached_WarnsButSucceeds()
    {
        _workItemRepo.ExistsByIdAsync(999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _cmd.LinkAsync(-1, 999, null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("related")]
    [InlineData("blocks")]
    [InlineData("blocked-by")]
    [InlineData("depends-on")]
    [InlineData("depended-on-by")]
    [InlineData("parent-child")]
    public async Task Link_AllValidTypes_Accepted(string linkType)
    {
        var result = await _cmd.LinkAsync(-1, -2, linkType);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Link_PositiveSourceNegativeTarget_Succeeds()
    {
        _workItemRepo.ExistsByIdAsync(42, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _cmd.LinkAsync(42, -1, null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.SourceId == 42 && l.TargetId == -1),
            Arg.Any<CancellationToken>());
    }

    // ── Cycle detection tests ───────────────────────────────────────

    [Fact]
    public async Task Link_DirectCycle_Rejected()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        });

        var result = await _cmd.LinkAsync(-2, -1, "blocks");

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_TransitiveCycle_Rejected()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        var result = await _cmd.LinkAsync(-3, -1, "depends-on");

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_SelfLoop_DirectionalType_Rejected()
    {
        var result = await _cmd.LinkAsync(-1, -1, "depends-on");

        result.ShouldBe(1);
        // Self-loop shortcut should not call GetSeedsAsync
        await _workItemRepo.DidNotReceive().GetSeedsAsync(Arg.Any<CancellationToken>());
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_RelatedType_BypassesCycleDetection()
    {
        // Even with a graph that would cycle for directional types, Related is non-directional
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        var result = await _cmd.LinkAsync(-2, -1, "related");

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Any<SeedLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_NonCyclicDirectionalLink_Succeeds()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());

        var result = await _cmd.LinkAsync(-1, -2, "blocks");

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.SourceId == -1 && l.TargetId == -2 && l.LinkType == SeedLinkTypes.Blocks),
            Arg.Any<CancellationToken>());
    }

    // ── UnlinkAsync tests ───────────────────────────────────────────

    [Fact]
    public async Task Unlink_ExistingLink_Succeeds()
    {
        var result = await _cmd.UnlinkAsync(-1, -2, null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemoveLinkAsync(-1, -2, SeedLinkTypes.Related, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlink_WithType_UsesSpecifiedType()
    {
        var result = await _cmd.UnlinkAsync(-1, -2, "blocks");

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemoveLinkAsync(-1, -2, SeedLinkTypes.Blocks, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlink_NonExistent_SucceedsWithNoOp()
    {
        // RemoveLinkAsync doesn't throw for non-existent links — DELETE just affects 0 rows
        var result = await _cmd.UnlinkAsync(-1, -99, null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemoveLinkAsync(-1, -99, SeedLinkTypes.Related, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlink_InvalidType_Rejected()
    {
        var result = await _cmd.UnlinkAsync(-1, -2, "invalid");

        result.ShouldBe(1);
        await _seedLinkRepo.DidNotReceive().RemoveLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── ListLinksAsync tests ────────────────────────────────────────

    [Fact]
    public async Task ListLinks_AllLinks_ReturnsAll()
    {
        var links = new List<SeedLink>
        {
            new(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            new(-1, 100, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);

        var result = await _cmd.ListLinksAsync(null);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).GetAllSeedLinksAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListLinks_ById_ReturnsLinksForItem()
    {
        var links = new List<SeedLink>
        {
            new(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(-1, Arg.Any<CancellationToken>()).Returns(links);

        var result = await _cmd.ListLinksAsync(-1);

        result.ShouldBe(0);
        await _seedLinkRepo.Received(1).GetLinksForItemAsync(-1, Arg.Any<CancellationToken>());
        await _seedLinkRepo.DidNotReceive().GetAllSeedLinksAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListLinks_EmptyResult_Succeeds()
    {
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());

        var result = await _cmd.ListLinksAsync(null);

        result.ShouldBe(0);
    }

    // ── Formatter tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task ListLinks_AllFormats_Succeed(string format)
    {
        var links = new List<SeedLink>
        {
            new(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);

        var result = await _cmd.ListLinksAsync(null, format);

        result.ShouldBe(0);
    }
}
