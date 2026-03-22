using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for StashCommand (EPIC-006 ITEM-036).
/// </summary>
public class StashCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IGitService _gitService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;

    public StashCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _gitService = Substitute.For<IGitService>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
    }

    private StashCommand CreateCommand(IGitService? git = null) =>
        new(_contextStore, _workItemRepo,
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _formatterFactory, _hintEngine, _config, git);

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.UserStory,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Stash happy path ────────────────────────────────────────────

    [Fact]
    public async Task Stash_WithWorkItem_IncludesContextInMessage()
    {
        var item = CreateWorkItem(42, "Login feature");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("wip");

        result.ShouldBe(0);
        await _gitService.Received().StashAsync(
            Arg.Is<string>(s => s.Contains("#42") && s.Contains("Login feature") && s.Contains("wip")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stash_WithWorkItem_NoUserMessage_UsesContextOnly()
    {
        var item = CreateWorkItem(42, "Login feature");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _gitService.Received().StashAsync(
            Arg.Is<string>(s => s.Contains("#42") && s.Contains("Login feature")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stash_NoWorkItem_UsesDefaultMessage()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _gitService.Received().StashAsync(
            Arg.Is<string>(s => s == "twig stash"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stash_NoWorkItem_WithMessage_UsesUserMessage()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("custom message");

        result.ShouldBe(0);
        await _gitService.Received().StashAsync("custom message", Arg.Any<CancellationToken>());
    }

    // ── Stash cache miss — auto-fetch from ADO ────────────────────

    [Fact]
    public async Task Stash_CacheMiss_AutoFetchesFromAdo()
    {
        var item = CreateWorkItem(42, "Auto-fetched");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("wip");

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
        await _gitService.Received().StashAsync(
            Arg.Is<string>(s => s.Contains("#42") && s.Contains("Auto-fetched") && s.Contains("wip")),
            Arg.Any<CancellationToken>());
    }

    // ── Stash unreachable — ADO fetch fails ─────────────────────────

    [Fact]
    public async Task Stash_Unreachable_ReturnsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("wip");

        result.ShouldBe(1);
        await _gitService.DidNotReceive().StashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Stash no git ────────────────────────────────────────────────

    [Fact]
    public async Task Stash_NoGitService_ReturnsError()
    {
        var cmd = CreateCommand(git: null);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Stash_NotInWorkTree_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Stash_GitThrows_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.StashAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("nothing to stash"));

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    // ── Pop happy path ──────────────────────────────────────────────

    [Fact]
    public async Task Pop_Success_ReturnsZero()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.PopAsync();

        result.ShouldBe(0);
        await _gitService.Received().StashPopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pop_RestoresContextFromBranch()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.PopAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(12345, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pop_NoMatchingWorkItem_DoesNotSetContext()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.PopAsync();

        result.ShouldBe(0);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Pop no git ──────────────────────────────────────────────────

    [Fact]
    public async Task Pop_NoGitService_ReturnsError()
    {
        var cmd = CreateCommand(git: null);
        var result = await cmd.PopAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Pop_GitPopFails_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.StashPopAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no stash entries"));

        var cmd = CreateCommand(_gitService);
        var result = await cmd.PopAsync();
        result.ShouldBe(1);
    }

    // ── Output formats ──────────────────────────────────────────────

    [Fact]
    public async Task Stash_JsonOutput_ContainsStructuredFields()
    {
        var item = CreateWorkItem(42, "Login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(outputFormat: "json");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Stash_MinimalOutput_PrintsMessage()
    {
        var item = CreateWorkItem(42, "Login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(outputFormat: "minimal");
        result.ShouldBe(0);
    }
}
