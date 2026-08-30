using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// AB#735 criterion (c): the PUBLISH flow rejects a non-sprint-tier item being
/// committed to a sprint iteration, decided through the T3 profile seam.
/// </summary>
/// <remarks>
/// Asserted at the orchestrator because every publish path funnels through it —
/// <c>twig new</c> followed by <c>seed publish</c>, <c>seed publish --all</c>,
/// and the plan <c>publish-seed</c> operation (which delegates here via
/// <c>PlanSeedPublisher</c>). A gate placed at any one command would leave the
/// other two open.
/// </remarks>
public sealed class SeedPublishSprintEntryTests
{
    private const int PublishedId = 4242;

    private readonly IWorkItemRepository _repo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly ISeedLinkRepository _seedLinks = Substitute.For<ISeedLinkRepository>();
    private readonly IWorkItemLinkRepository _itemLinks = Substitute.For<IWorkItemLinkRepository>();
    private readonly IPublishIdMapRepository _idMap = Substitute.For<IPublishIdMapRepository>();
    private readonly ISeedPublishRulesProvider _rules = Substitute.For<ISeedPublishRulesProvider>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IFieldDefinitionStore _fields = Substitute.For<IFieldDefinitionStore>();

    public SeedPublishSprintEntryTests()
    {
        _uow.BeginAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        _rules.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);
        _ado.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(PublishedId);
    }

    private SeedPublishOrchestrator Orchestrator(SprintEntryPolicy policy) =>
        new(_repo, _ado, _seedLinks, _itemLinks, _idMap, _rules, _uow,
            new BacklogOrderer(_ado, _fields), Substitute.For<IPendingChangeStore>(),
            publishIntentRepo: null, sprintEntryPolicy: policy);

    private void ArrangeSeed(WorkItemType type, string iteration)
    {
        var seed = new WorkItemBuilder(-1, "candidate")
            .AsType(type)
            .WithIterationPath(iteration)
            .AsSeed()
            .Build();
        _repo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedLinks.GetLinksForItemAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
    }

    [Fact]
    public async Task Non_sprint_tier_seed_targeting_a_sprint_is_refused_before_any_ado_call()
    {
        ArrangeSeed(WorkItemType.Feature, @"Twig\Sprint 1");

        var result = await Orchestrator(ReferenceProfileBuilder.SprintPolicy()).PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        result.ValidationFailures.ShouldContain(f =>
            f.Rule == "System.IterationPath"
            && f.Message.Contains(SprintEntryFailure.NotSprintTier, StringComparison.Ordinal));

        // The refusal must precede the create. Refusing after the work item
        // exists in ADO would leave an orphan that the retry path then has to
        // reconcile — the exact failure shape the publish-intent ledger exists
        // to avoid.
        await _ado.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sprint_tier_seed_targeting_a_sprint_publishes()
    {
        ArrangeSeed(WorkItemType.Task, @"Twig\Sprint 1");
        _ado.FetchAsync(PublishedId, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(PublishedId, "candidate").AsTask().Build());

        var result = await Orchestrator(ReferenceProfileBuilder.SprintPolicy()).PublishAsync(-1);

        result.Status.ShouldNotBe(SeedPublishStatus.ValidationFailed);
        await _ado.Received(1).CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Non_sprint_tier_seed_targeting_the_backlog_root_publishes()
    {
        ArrangeSeed(WorkItemType.Feature, "Twig");
        _ado.FetchAsync(PublishedId, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(PublishedId, "candidate").AsFeature().Build());

        var result = await Orchestrator(ReferenceProfileBuilder.SprintPolicy()).PublishAsync(-1);

        result.Status.ShouldNotBe(SeedPublishStatus.ValidationFailed);
        await _ado.Received(1).CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <c>--force</c> bypasses the repository's own publish RULES, not the
    /// reference process's structural invariants. ADO's backlog behaviour
    /// refuses the write anyway, so forcing it would only convert a clear local
    /// refusal into a remote error after the create was attempted.
    /// </summary>
    [Fact]
    public async Task Force_does_not_bypass_the_sprint_entry_invariant()
    {
        ArrangeSeed(WorkItemType.Epic, @"Twig\Sprint 1");

        var result = await Orchestrator(ReferenceProfileBuilder.SprintPolicy())
            .PublishAsync(-1, force: true);

        result.Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        await _ado.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The gate is answered by the profile, so rebinding the leaf role moves it.
    /// This is what makes the check a seam consumer rather than a literal.
    /// </summary>
    [Fact]
    public async Task Gate_tracks_the_profiles_leaf_binding()
    {
        ArrangeSeed(WorkItemType.Task, @"Twig\Sprint 1");

        var result = await Orchestrator(ReferenceProfileBuilder.SprintPolicy(taskTypeName: "Chore"))
            .PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        await _ado.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }
}
