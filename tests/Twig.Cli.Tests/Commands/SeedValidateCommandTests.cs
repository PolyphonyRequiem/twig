using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedValidateCommandTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly SeedValidateCommand _cmd;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SeedValidateCommandTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _cmd = new SeedValidateCommand(_workItemRepo, _rulesProvider, _formatterFactory);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single seed validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateSingle_PassingSeed_Returns0()
    {
        var seed = new WorkItemBuilder(-1, "Good seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-1, "human");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("✔");
        writer.ToString().ShouldContain("Good seed");
    }

    [Fact]
    public async Task ValidateSingle_FailingSeed_Returns1()
    {
        var seed = new WorkItemBuilder(-2, "").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-2, "human");

        result.ShouldBe(1);
        writer.ToString().ShouldContain("✘");
    }

    [Fact]
    public async Task ValidateSingle_SeedNotFound_Returns1()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-99, "human");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("not found");
    }

    [Fact]
    public async Task ValidateSingle_NonSeedItem_Returns1()
    {
        var item = new WorkItemBuilder(100, "Not a seed").Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(100, "human");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  All seeds validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateAll_AllPass_Returns0()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Seed A").AsSeed().Build(),
            new WorkItemBuilder(-2, "Seed B").AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(null, "human");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("2/2 passed");
    }

    [Fact]
    public async Task ValidateAll_SomeFail_Returns1()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Good seed").AsSeed().Build(),
            new WorkItemBuilder(-2, "").AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(null, "human");

        result.ShouldBe(1);
        writer.ToString().ShouldContain("1/2 passed");
    }

    [Fact]
    public async Task ValidateAll_NoSeeds_Returns0()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(null, "human");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No seeds");
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateSingle_JsonOutput_ContainsStructuredData()
    {
        var seed = new WorkItemBuilder(-1, "JSON seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-1, "json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"passed\": true");
        output.ShouldContain("\"seedId\": -1");
    }

    [Fact]
    public async Task ValidateAll_JsonOutput_ContainsResultArray()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Seed A").AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(null, "json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"results\"");
        output.ShouldContain("\"total\": 1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Minimal output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateSingle_MinimalOutput_ShowsPassFail()
    {
        var seed = new WorkItemBuilder(-1, "Min seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-1, "minimal");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("PASS");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom rules
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ValidateSingle_CustomRulesRequireParent_OrphanFails()
    {
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new SeedPublishRules
            {
                RequiredFields = ["System.Title"],
                RequireParent = true,
            });

        var seed = new WorkItemBuilder(-1, "Orphan seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-1, "human");

        result.ShouldBe(1);
        writer.ToString().ShouldContain("parent");
    }
}
