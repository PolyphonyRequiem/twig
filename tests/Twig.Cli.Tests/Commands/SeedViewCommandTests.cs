using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedViewCommandTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RenderingPipelineFactory _renderingPipelineFactory;
    private readonly SeedViewCommand _cmd;
    private readonly TextWriter _originalOut;

    public SeedViewCommandTests()
    {
        _originalOut = Console.Out;
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _config = new TwigConfiguration();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());

        // Force sync rendering (no live renderer) by using redirected output
        _renderingPipelineFactory = new RenderingPipelineFactory(
            _formatterFactory,
            Substitute.For<IAsyncRenderer>(),
            isOutputRedirected: () => true);

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<FieldDefinition>
        {
            new("System.Title", "Title", "string", false),
            new("System.Description", "Description", "html", false),
            new("System.State", "State", "string", true),
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance Criteria", "html", false),
            new("System.AreaPath", "Area Path", "treePath", true),
            new("System.IterationPath", "Iteration Path", "treePath", true),
            new("System.Rev", "Rev", "integer", true),
        });

        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        _cmd = new SeedViewCommand(
            _workItemRepo,
            _fieldDefStore,
            _seedLinkRepo,
            _config,
            _renderingPipelineFactory);
    }

    [Fact]
    public async Task EmptySeeds_ShowsNoSeeds()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No seeds");
    }

    [Fact]
    public async Task MultipleParents_GroupedCorrectly()
    {
        var parent1 = CreateWorkItem(123, "Login flow", "User Story");
        var parent2 = CreateWorkItem(456, "Payment integration", "Feature");

        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Implement OAuth callback", "Task", parentId: 123),
            CreateSeed(-2, "Add token refresh logic", "Task", parentId: 123),
            CreateSeed(-3, "Stripe webhook handler", "User Story", parentId: 456),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _workItemRepo.GetByIdAsync(123, Arg.Any<CancellationToken>()).Returns(parent1);
        _workItemRepo.GetByIdAsync(456, Arg.Any<CancellationToken>()).Returns(parent2);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Seeds (3)");
        output.ShouldContain("#123");
        output.ShouldContain("Login flow");
        output.ShouldContain("#456");
        output.ShouldContain("Payment integration");
        output.ShouldContain("Implement OAuth callback");
        output.ShouldContain("Stripe webhook handler");
    }

    [Fact]
    public async Task OrphanSeeds_GroupedUnderOrphanSeeds()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-4, "Q3 Planning", "Epic", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Orphan Seeds");
        output.ShouldContain("Q3 Planning");
    }

    [Fact]
    public async Task StaleDetection_BasedOnStaleDays()
    {
        _config.Seed.StaleDays = 7;

        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Stale seed", "Task", parentId: null,
                createdAt: DateTimeOffset.UtcNow.AddDays(-10)),
            CreateSeed(-2, "Fresh seed", "Task", parentId: null,
                createdAt: DateTimeOffset.UtcNow.AddDays(-2)),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        // Stale seed should have stale warning
        output.ShouldContain("stale");
    }

    [Fact]
    public async Task FieldCompleteness_CalculatedCorrectly()
    {
        // The field def store returns 5 writable fields (Title, Description, Priority, AcceptanceCriteria, and... let's count)
        // ReadOnly fields: State, AreaPath, IterationPath, Rev = 4 readonly
        // Writable fields: Title, Description, Priority, AcceptanceCriteria = 4 writable
        // A seed with 2 filled fields should show 2/4

        var seed = CreateSeed(-1, "Test seed", "Task", parentId: null);
        // Add some field values
        seed.ImportFields(new Dictionary<string, string?>
        {
            { "System.Description", "Some description" },
            { "Microsoft.VSTS.Common.Priority", "1" },
        });

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("2/4 fields");
    }

    [Fact]
    public async Task JsonFormat_ProducesStructuredOutput()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Json test seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"groups\"");
        output.ShouldContain("\"totalSeeds\"");
        output.ShouldContain("\"seeds\"");
        output.ShouldContain("Json test seed");
    }

    [Fact]
    public async Task MinimalFormat_ProducesCompactOutput()
    {
        var parent = CreateWorkItem(10, "Parent item", "User Story");
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Minimal test", "Task", parentId: 10),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(parent);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("minimal");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("SEED #-1");
        output.ShouldContain("Minimal test");
        output.ShouldContain("parent:#10");
    }

    [Fact]
    public async Task MinimalFormat_EmptySeeds_ShowsNoSeeds()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("minimal");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No seeds");
    }

    [Fact]
    public async Task MixedParentedAndOrphan_BothGroupsPresent()
    {
        var parent = CreateWorkItem(100, "Parent WI", "Feature");
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Parented seed", "Task", parentId: 100),
            CreateSeed(-2, "Orphan seed", "Bug", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Seeds (2)");
        output.ShouldContain("Parent WI");
        output.ShouldContain("Orphan Seeds");
        output.ShouldContain("Parented seed");
        output.ShouldContain("Orphan seed");
    }

    [Fact]
    public async Task NullSeedCreatedAt_ShowsQuestionMarkAge()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "No date seed", "Task", parentId: null, nullCreatedAt: true),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        // Human format
        var writer = new StringWriter();
        Console.SetOut(writer);
        var result = await _cmd.ExecuteAsync("human");
        result.ShouldBe(0);
        writer.ToString().ShouldContain("?d ago");

        // Minimal format
        writer = new StringWriter();
        Console.SetOut(writer);
        result = await _cmd.ExecuteAsync("minimal");
        result.ShouldBe(0);
        writer.ToString().ShouldContain("?d ago");

        // JSON format
        writer = new StringWriter();
        Console.SetOut(writer);
        result = await _cmd.ExecuteAsync("json");
        result.ShouldBe(0);
        var jsonOutput = writer.ToString();
        jsonOutput.ShouldContain("\"seedCreatedAt\": null");
        jsonOutput.ShouldContain("\"age\": \"?d ago\"");
    }

    [Fact]
    public async Task HumanFormat_SeedIdIncludesHashPrefix()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Hash test seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("#-1");
    }

    // ── Link display tests ──────────────────────────────────────────

    [Fact]
    public async Task HumanFormat_ShowsLinksPerSeed()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Linked seed", "Task", parentId: null),
            CreateSeed(-2, "Target seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>
            {
                new(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
            });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("→ blocks -2");
        output.ShouldContain("→ blocked by -1");
    }

    [Fact]
    public async Task HumanFormat_DependsOnLink_ShowsCorrectAnnotation()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Depends seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>
            {
                new(-1, 12345, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("→ depends on #12345");
    }

    [Fact]
    public async Task HumanFormat_NoLinks_ShowsNoLinkAnnotation()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "No links seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldNotContain("→");
    }

    [Fact]
    public async Task JsonFormat_IncludesLinksPerSeed()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Json linked", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>
            {
                new(-1, -3, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"links\"");
        output.ShouldContain("\"linkType\": \"related\"");
        output.ShouldContain("\"annotation\": \"related -3\"");
    }

    [Fact]
    public async Task JsonFormat_NoLinks_EmptyLinksArray()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "No links json", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"links\": []");
    }

    [Fact]
    public async Task MinimalFormat_ShowsLinksPerSeed()
    {
        var parent = CreateWorkItem(10, "Parent item", "User Story");
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Minimal linked", "Task", parentId: 10),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(parent);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>
            {
                new(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
            });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("minimal");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("LINK → blocks -2");
    }

    [Fact]
    public async Task MinimalFormat_NoLinks_NoLinkLines()
    {
        var seeds = new List<WorkItem>
        {
            CreateSeed(-1, "Unconnected seed", "Task", parentId: null),
        };

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync("minimal");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldNotContain("LINK →");
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, string type)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateSeed(int id, string title, string type, int? parentId,
        DateTimeOffset? createdAt = null, bool nullCreatedAt = false)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = nullCreatedAt ? null : createdAt ?? DateTimeOffset.UtcNow,
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
