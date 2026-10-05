using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.Cli.Tests.TestSupport;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Workspace self-view contracts after ADO #1106 / Spec #1103.
/// <para>
/// 🔴 The self default view is bound to the authenticated connection's canonical
/// <c>uniqueName</c>, never to the ambient display name. A connection without a bound principal
/// REFUSES rather than widening to the whole team or falling back to display. <c>--all</c> is the
/// explicit team view and does not consult the bound principal, so a missing identity does not
/// block it.
/// </para>
/// </summary>
public class UserScopedWorkspaceTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationService _iterationService;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly ITrackingService _trackingService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public UserScopedWorkspaceTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _workItemRepo.BridgeBatchIterationReads();
        _iterationService = Substitute.For<IIterationService>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService);
        _trackingService = Substitute.For<ITrackingService>();
        _trackingService.GetTrackedItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private CommandContext CreateCtx() =>
        new(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            _hintEngine,
            new TwigConfiguration());

    private WorkspaceCommand CreateCommand() =>
        new(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder(),
            new SprintIterationResolver(_iterationService, _workItemRepo));

    [Fact]
    public async Task Self_DefaultView_BindsToCanonicalPrincipal_NotDisplayName()
    {
        _iterationService.WithBoundIdentity(uniqueName: "alice@contoso.com");

        // Two accounts share the display label "Alex Smith" but carry different canonical identities.
        var selfAlice = CreateWorkItem(1, "Alice's task",
            assignedToDisplay: "Alex Smith", assignedToUniqueName: "alice@contoso.com");
        var otherAlex = CreateWorkItem(2, "Alex's task",
            assignedToDisplay: "Alex Smith", assignedToUniqueName: "alex@fabrikam.com");
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { selfAlice, otherAlex });

        var result = await CreateCommand().ExecuteAsync();

        result.ShouldBe(0);
        // The repo is READ unfiltered — a server-side display filter would silently merge the two.
        await _workItemRepo.Received().GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
        // A display-name filter would call this; the canonical filter does not.
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task All_ExplicitlyBypassesSelfFilter()
    {
        _iterationService.WithBoundIdentity();

        var aliceItem = CreateWorkItem(1, "Task A",
            assignedToDisplay: "Alice", assignedToUniqueName: IdentityStubs.DefaultCanonicalPrincipal);
        var bobItem = CreateWorkItem(2, "Task B",
            assignedToDisplay: "Bob", assignedToUniqueName: "bob@contoso.com");
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem, bobItem });

        var result = await CreateCommand().ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task All_StillWorks_WhenConnectionCannotProveCanonicalIdentity()
    {
        // Explicit team view does NOT consult the bound principal, so a connection whose identity
        // is unresolved does not block --all. The self fail-closed path never runs here.
        _iterationService.WithoutBoundIdentity();

        var items = new[]
        {
            CreateWorkItem(1, "Team A", "Alice", "alice@contoso.com"),
            CreateWorkItem(2, "Team B", "Bob", "bob@contoso.com"),
        };
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await CreateCommand().ExecuteAsync(all: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Self_WithoutBoundIdentity_RefusesInsteadOfWideningToTeam()
    {
        _iterationService.WithoutBoundIdentity();

        var items = new[]
        {
            CreateWorkItem(1, "Team A", "Alice", "alice@contoso.com"),
        };
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(items);

        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            var result = await CreateCommand().ExecuteAsync();
            // Non-zero exit: the view REFUSES; it does not widen to the whole team.
            result.ShouldBe(1);
            stderr.ToString().ShouldContain(DefaultBenchSelectors.MissingBoundIdentityMessage);
        }
        finally
        {
            Console.SetError(original);
        }
    }


    private static WorkItem CreateWorkItem(
        int id,
        string title,
        string? assignedToDisplay = null,
        string? assignedToUniqueName = null)
        => new()
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            AssignedTo = assignedToDisplay,
            AssignedToUniqueName = assignedToUniqueName,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
}
