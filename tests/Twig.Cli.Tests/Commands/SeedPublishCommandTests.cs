using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedPublishCommandTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IContextStore _contextStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly SeedPublishCommand _cmd;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SeedPublishCommandTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
        _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _contextStore = Substitute.For<IContextStore>();

        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var tx = Substitute.For<ITransaction>();
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(tx);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer);

        _cmd = new SeedPublishCommand(orchestrator, _contextStore, _formatterFactory, _adoService);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No ID, no --all → error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_NoIdNoAll_Returns1()
    {
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync(null, all: false);

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("Specify a seed ID");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single seed publish — success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_SingleSeed_PublishesAndOutputsSuccess()
    {
        var seed = new WorkItemBuilder(-5, "My Task").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "My Task").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Published seed #-5 as #42");
        output.ShouldContain("My Task");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single seed — parent not published
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_ParentNotPublished_Returns1WithMessage()
    {
        var seed = new WorkItemBuilder(-5, "Child Task").AsSeed().WithParent(-3).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(1);
        writer.ToString().ShouldContain("published first");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single seed — seed not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_SeedNotFound_Returns1()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-99);

        result.ShouldBe(1);
        writer.ToString().ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dry run — no API calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_DryRun_ShowsPlanWithoutApiCalls()
    {
        var seed = new WorkItemBuilder(-5, "Dry Run Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5, dryRun: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("dry-run");
        writer.ToString().ShouldContain("Dry Run Seed");
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Force — skips validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Force_SkipsValidation()
    {
        // A seed with empty title would normally fail validation
        var seed = new WorkItemBuilder(-5, "").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(50);

        var published = new WorkItemBuilder(50, "").WithParent(100).Build();
        _adoService.FetchAsync(50, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(50, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5, force: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("Published seed #-5 as #50");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation failure without force
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_ValidationFails_Returns1()
    {
        var seed = new WorkItemBuilder(-5, "").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5);

        result.ShouldBe(1);
        writer.ToString().ShouldContain("failed validation");
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Positive ID — already published, skips
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_PositiveId_SkipsGracefully()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("already published");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Publish all — batch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_PublishesAllSeeds()
    {
        var parent = new WorkItemBuilder(-1, "Parent Seed").AsSeed().WithParent(100).Build();
        var child = new WorkItemBuilder(-2, "Child Seed").AsSeed().WithParent(-1).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { parent, child });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        // Parent publish
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.CreateAsync(parent, Arg.Any<CancellationToken>()).Returns(200);
        var publishedParent = new WorkItemBuilder(200, "Parent Seed").WithParent(100).Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(publishedParent);
        _seedLinkRepo.GetLinksForItemAsync(200, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        // After parent publishes, child's parent ID is remapped to 200
        var remappedChild = new WorkItemBuilder(-2, "Child Seed").AsSeed().WithParent(200).Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(remappedChild);
        _adoService.CreateAsync(remappedChild, Arg.Any<CancellationToken>()).Returns(201);
        var publishedChild = new WorkItemBuilder(201, "Child Seed").WithParent(200).Build();
        _adoService.FetchAsync(201, Arg.Any<CancellationToken>()).Returns(publishedChild);
        _seedLinkRepo.GetLinksForItemAsync(201, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Parent Seed");
        output.ShouldContain("Child Seed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Publish all — no seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_AllNoSeeds_Returns0()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No seeds");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Publish all — dry run
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_AllDryRun_ShowsPlanWithoutApiCalls()
    {
        var seed = new WorkItemBuilder(-1, "Dry Run Batch").AsSeed().WithParent(100).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true, dryRun: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("dry-run");
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_SingleSeed_JsonOutput()
    {
        var seed = new WorkItemBuilder(-5, "JSON Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "JSON Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5, outputFormat: "json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"oldId\": -5");
        output.ShouldContain("\"newId\": 42");
        output.ShouldContain("\"status\": \"Created\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Minimal output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_SingleSeed_MinimalOutput()
    {
        var seed = new WorkItemBuilder(-5, "Min Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "Min Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5, outputFormat: "minimal");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("PUBLISH #-5 => #42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  --link-branch parameter wiring — does not break publish
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_LinkBranch_SingleSeed_PublishesSuccessfully()
    {
        var seed = new WorkItemBuilder(-5, "Branch Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "Branch Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(-5, linkBranch: "planning/abc");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("Published seed #-5 as #42");
    }

    [Fact]
    public async Task Execute_LinkBranch_All_PublishesSuccessfully()
    {
        var seed = new WorkItemBuilder(-1, "Batch Branch Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(200);
        var published = new WorkItemBuilder(200, "Batch Branch Seed").WithParent(100).Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(200, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true, linkBranch: "planning/abc");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("Batch Branch Seed");
    }

    [Fact]
    public async Task Execute_LinkBranch_NullAdoGitService_StillPublishes()
    {
        // SeedPublishCommand constructed with null adoGitService (default) should
        // still publish seeds successfully when --link-branch is passed.
        var seed = new WorkItemBuilder(-5, "No Git Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "No Git Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var writer = new StringWriter();
        var errWriter = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(errWriter);

        var result = await _cmd.ExecuteAsync(-5, linkBranch: "feature/xyz");

        result.ShouldBe(0);
        writer.ToString().ShouldContain("Published seed #-5 as #42");
        errWriter.ToString().ShouldContain("git service configuration");
    }

    // ═══════════════════════════════════════════════════════════════
    //  --link-branch: actual linking with IAdoGitService
    // ═══════════════════════════════════════════════════════════════

    private SeedPublishCommand CreateCommandWithGitService(IAdoGitService gitService)
    {
        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefStore);
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, _seedLinkRepo, _publishIdMapRepo,
            _rulesProvider, _unitOfWork, backlogOrderer);
        return new SeedPublishCommand(orchestrator, _contextStore, _formatterFactory, _adoService, gitService);
    }

    [Fact]
    public async Task Execute_LinkBranch_SingleSeed_CallsAddArtifactLinkAsync()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        var cmd = CreateCommandWithGitService(gitService);

        var seed = new WorkItemBuilder(-5, "Link Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "Link Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        Console.SetOut(new StringWriter());

        var result = await cmd.ExecuteAsync(-5, linkBranch: "planning/abc");

        result.ShouldBe(0);
        await gitService.Received(1).AddArtifactLinkAsync(
            42,
            "vstfs:///Git/Ref/proj-id/repo-id/GBplanning%2Fabc",
            "ArtifactLink",
            Arg.Any<int>(),
            "Branch",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_LinkBranch_All_LinksAllCreatedSeeds()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        var cmd = CreateCommandWithGitService(gitService);

        var seed1 = new WorkItemBuilder(-1, "Seed A").AsSeed().WithParent(100).Build();
        var seed2 = new WorkItemBuilder(-2, "Seed B").AsSeed().WithParent(100).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed1, seed2 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed1);
        _adoService.CreateAsync(seed1, Arg.Any<CancellationToken>()).Returns(200);
        var pub1 = new WorkItemBuilder(200, "Seed A").WithParent(100).Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(pub1);
        _seedLinkRepo.GetLinksForItemAsync(200, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed2);
        _adoService.CreateAsync(seed2, Arg.Any<CancellationToken>()).Returns(201);
        var pub2 = new WorkItemBuilder(201, "Seed B").WithParent(100).Build();
        _adoService.FetchAsync(201, Arg.Any<CancellationToken>()).Returns(pub2);
        _seedLinkRepo.GetLinksForItemAsync(201, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        Console.SetOut(new StringWriter());

        var result = await cmd.ExecuteAsync(all: true, linkBranch: "feature/test");

        result.ShouldBe(0);
        await gitService.Received(2).AddArtifactLinkAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            "ArtifactLink",
            Arg.Any<int>(),
            "Branch",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_LinkBranch_LinkFailure_StillReturnsSuccess()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        gitService.AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("ADO error"));
        var cmd = CreateCommandWithGitService(gitService);

        var seed = new WorkItemBuilder(-5, "Fail Link Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "Fail Link Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var errWriter = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(errWriter);

        var result = await cmd.ExecuteAsync(-5, linkBranch: "planning/abc");

        result.ShouldBe(0);
        errWriter.ToString().ShouldContain("Failed to link branch to #42");
    }

    [Fact]
    public async Task Execute_LinkBranch_DryRun_NoLinkingCalls()
    {
        var gitService = Substitute.For<IAdoGitService>();
        var cmd = CreateCommandWithGitService(gitService);

        var seed = new WorkItemBuilder(-5, "DryRun Link Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);

        Console.SetOut(new StringWriter());

        var result = await cmd.ExecuteAsync(-5, dryRun: true, linkBranch: "planning/abc");

        result.ShouldBe(0);
        await gitService.DidNotReceive().GetProjectIdAsync(Arg.Any<CancellationToken>());
        await gitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_LinkBranch_NullProjectId_SkipsLinking()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        var cmd = CreateCommandWithGitService(gitService);

        var seed = new WorkItemBuilder(-5, "NullProj Seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(seed, Arg.Any<CancellationToken>()).Returns(42);

        var published = new WorkItemBuilder(42, "NullProj Seed").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(published);
        _seedLinkRepo.GetLinksForItemAsync(42, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        var errWriter = new StringWriter();
        Console.SetOut(new StringWriter());
        Console.SetError(errWriter);

        var result = await cmd.ExecuteAsync(-5, linkBranch: "planning/abc");

        result.ShouldBe(0);
        errWriter.ToString().ShouldContain("project/repository IDs");
        await gitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_LinkBranch_SkippedSeed_NotLinked()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        var cmd = CreateCommandWithGitService(gitService);

        // Positive ID is treated as already published => Skipped status
        Console.SetOut(new StringWriter());

        var result = await cmd.ExecuteAsync(42, linkBranch: "planning/abc");

        result.ShouldBe(0);
        await gitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_LinkBranch_UpfrontValidation_ResolvesOnce()
    {
        var gitService = Substitute.For<IAdoGitService>();
        gitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        gitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        var cmd = CreateCommandWithGitService(gitService);

        var seed1 = new WorkItemBuilder(-1, "Seed 1").AsSeed().WithParent(100).Build();
        var seed2 = new WorkItemBuilder(-2, "Seed 2").AsSeed().WithParent(100).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed1, seed2 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed1);
        _adoService.CreateAsync(seed1, Arg.Any<CancellationToken>()).Returns(200);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(200, "Seed 1").WithParent(100).Build());
        _seedLinkRepo.GetLinksForItemAsync(200, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed2);
        _adoService.CreateAsync(seed2, Arg.Any<CancellationToken>()).Returns(201);
        _adoService.FetchAsync(201, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(201, "Seed 2").WithParent(100).Build());
        _seedLinkRepo.GetLinksForItemAsync(201, Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());

        Console.SetOut(new StringWriter());

        await cmd.ExecuteAsync(all: true, linkBranch: "planning/abc");

        // ProjectId and RepoId should be resolved exactly once (upfront), not per-seed
        await gitService.Received(1).GetProjectIdAsync(Arg.Any<CancellationToken>());
        await gitService.Received(1).GetRepositoryIdAsync(Arg.Any<CancellationToken>());
    }
}

