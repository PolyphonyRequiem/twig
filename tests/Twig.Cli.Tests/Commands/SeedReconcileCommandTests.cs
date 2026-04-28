using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedReconcileCommandTests : IDisposable
{
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly SeedReconcileCommand _cmd;
    private readonly TextWriter _originalOut;

    public SeedReconcileCommandTests()
    {
        _originalOut = Console.Out;
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();

        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        var orchestrator = new SeedReconcileOrchestrator(
            _seedLinkRepo, _workItemRepo, _publishIdMapRepo);

        _cmd = new SeedReconcileCommand(orchestrator, _formatterFactory);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nothing to reconcile
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_NothingToDo_OutputsNothingToReconcile()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("human");

        exitCode.ShouldBe(0);
        writer.ToString().ShouldContain("Nothing to reconcile");
    }

    [Fact]
    public async Task Execute_NothingToDo_JsonOutput()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("json");

        exitCode.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"nothingToDo\": true");
        output.ShouldContain("\"linksRepaired\": 0");
    }

    [Fact]
    public async Task Execute_NothingToDo_MinimalOutput()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("minimal");

        exitCode.ShouldBe(0);
        writer.ToString().ShouldContain("RECONCILE NOTHING");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Repairs made
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_LinksRepaired_HumanOutputShowsCounts()
    {
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("human");

        exitCode.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Links repaired");
        output.ShouldContain("1");
    }

    [Fact]
    public async Task Execute_LinksRemoved_JsonOutputShowsCounts()
    {
        var links = new List<SeedLink>
        {
            new(-10, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("json");

        exitCode.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"linksRemoved\": 1");
    }

    [Fact]
    public async Task Execute_MinimalOutput_ShowsRepairCounts()
    {
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("minimal");

        exitCode.ShouldBe(0);
        writer.ToString().ShouldContain("RECONCILE REPAIRED 1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Always returns exit code 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_WithWarnings_StillReturns0()
    {
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var seed = new TestKit.WorkItemBuilder(-5, "Orphan").AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var exitCode = await _cmd.ExecuteAsync("human");

        exitCode.ShouldBe(0);
        writer.ToString().ShouldContain("discarded");
    }
}
