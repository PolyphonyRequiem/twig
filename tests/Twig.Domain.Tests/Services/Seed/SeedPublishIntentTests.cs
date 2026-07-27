using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// Regression tests for wayfinder 0015 — the durable intent record (0001 §4).
/// <para>
/// These cover the <b>7→10 window</b>: the ADO create at step 7 happens outside the
/// transaction that rolls back at step 10, so a crash in between orphans a real work item with
/// no local trace, and every retry creates another duplicate (PolyphonyRequiem/twig#270). #270
/// fixed the FK ordering inside step 10; the window itself stayed open until this ticket.
/// </para>
/// <para>
/// Each test here fails against the pre-0015 orchestrator: it had no intent repository, never
/// stamped an idempotency tag, and always called <c>CreateAsync</c> unconditionally.
/// </para>
/// </summary>
public class SeedPublishIntentTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IWorkItemLinkRepository _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly IPublishIdMapRepository _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
    private readonly ISeedPublishRulesProvider _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
    private readonly IPublishIntentRepository _intentRepo = Substitute.For<IPublishIntentRepository>();

    private readonly SeedPublishOrchestrator _orchestrator;
    private readonly StagedIdentity _identity = StagedIdentity.New();
    private readonly DateTimeOffset _recordedAt = DateTimeOffset.UtcNow;

    public SeedPublishIntentTests()
    {
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);

        // The default is "ADO has no matching in-flight item" — the ordinary first-attempt path.
        _adoService.FindPublishedIntentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns((int?)null);

        _intentRepo.RecordIntentAsync(
                Arg.Any<StagedIdentity>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new PublishIntent
            {
                Identity = ci.ArgAt<StagedIdentity>(0),
                Title = ci.ArgAt<string>(1),
                TypeName = ci.ArgAt<string>(2),
                RecordedAt = _recordedAt,
            });

        _orchestrator = new SeedPublishOrchestrator(
            _workItemRepo,
            _adoService,
            _seedLinkRepo,
            _workItemLinkRepo,
            _publishIdMapRepo,
            _rulesProvider,
            _unitOfWork,
            new BacklogOrderer(_adoService, _fieldDefinitionStore),
            pendingChangeStore: null,
            publishIntentRepo: _intentRepo);
    }

    private WorkItem ArrangeSeed()
    {
        var seed = new WorkItemBuilder(-1, "A staged seed")
            .AsTask()
            .AsSeed(stagedIdentity: _identity)
            .Build();

        // Fixture guard: the identity is what keys the intent. Without it the orchestrator
        // takes the pre-0014 unprotected path and every assertion below would pass vacuously.
        seed.StagedIdentity.ShouldBe(_identity);

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedLinkRepo.GetLinksForItemAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());

        var published = new WorkItemBuilder(500, "A staged seed").AsTask().Build();
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(published);
        _adoService.FetchWithLinksAsync(500, Arg.Any<CancellationToken>())
            .Returns((published, (IReadOnlyList<WorkItemLink>)[]));

        return seed;
    }

    // ═══════════════════════════════════════════════════════════════
    //  The intent is recorded BEFORE the ADO call, not after it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_RecordsIntentBeforeCreatingInAdo()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        Received.InOrder(() =>
        {
            _intentRepo.RecordIntentAsync(
                _identity, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PublishAsync_RecordsOutcomeAfterCreateSucceeds()
    {
        ArrangeSeed();

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        await _intentRepo.Received(1).CompleteIntentAsync(_identity, 500, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  The idempotency key: stamped on the create, so ADO can be asked
    //  later whether the create already happened
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_StampsTheIntentTagOnTheCreateRequest()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        await _adoService.Received(1).CreateAsync(
            Arg.Is<CreateWorkItemRequest>(r => r.StampIntentTag),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_ClearsTheIntentTagOnceThePublishIsRecorded()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        // The tag marks IN-FLIGHT state, not provenance. Leaving it behind would make every
        // published item look permanently in flight and would keep twig's private bookkeeping
        // visible in the project's shared tag vocabulary.
        Received.InOrder(() =>
        {
            _intentRepo.CompleteIntentAsync(_identity, 500, Arg.Any<CancellationToken>());
            _adoService.ClearIntentTagAsync(500, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PublishAsync_WhenClearingTheTagFails_ThePublishStillSucceeds()
    {
        ArrangeSeed();
        _adoService
            .When(a => a.ClearIntentTagAsync(500, Arg.Any<CancellationToken>()))
            .Do(_ => throw new HttpRequestException("ADO unreachable"));

        var result = await _orchestrator.PublishAsync(-1);

        // The publish has already succeeded by this point; a stale cosmetic tag must not turn
        // it into a reported failure.
        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
    }

    [Fact]
    public async Task PublishAsync_AsksAdoWhetherTheCreateAlreadyLanded_BeforeCreating()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        Received.InOrder(() =>
        {
            _adoService.FindPublishedIntentAsync(
                "A staged seed", Arg.Any<string>(), _recordedAt, Arg.Any<CancellationToken>());
            _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  THE BUG (#270): a retry after a crash in the 7→10 window must
    //  adopt the orphan, not create a second one
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_WhenAPriorAttemptAlreadyCreatedTheItem_DoesNotCreateADuplicate()
    {
        ArrangeSeed();

        // The previous attempt's create landed in ADO but the process died before step 10
        // committed. The stamped tag is the only evidence it happened.
        _adoService.FindPublishedIntentAsync(
                "A staged seed", Arg.Any<string>(), _recordedAt, Arg.Any<CancellationToken>())
            .Returns(500);

        var result = await _orchestrator.PublishAsync(-1);

        // This is the assertion that fails on the unfixed code: it called CreateAsync
        // unconditionally, producing the duplicate #270 describes.
        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);

        // And the orphan is now accounted for locally — the window is closed.
        await _intentRepo.Received(1).CompleteIntentAsync(_identity, 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_WhenTheLocalTransactionFails_TheIntentOutcomeSurvives()
    {
        ArrangeSeed();

        // Reproduce the #270 shape: the ADO item exists, then the local half throws.
        _workItemRepo
            .When(r => r.DeleteByIdAsync(-1, Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("FOREIGN KEY constraint failed"));

        await Should.ThrowAsync<InvalidOperationException>(() => _orchestrator.PublishAsync(-1));

        // The intent was completed OUTSIDE the transaction, so the rollback cannot erase it.
        // Without this, the created item is invisible locally and the retry duplicates it.
        await _intentRepo.Received(1).CompleteIntentAsync(_identity, 500, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).RollbackAsync(_transaction, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tag shape — it must stay queryable in ADO
    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════
    //  Tag CARDINALITY — the project tag vocabulary is a SHARED resource
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IntentTag_IsAConstant_SoPublishingDoesNotGrowTheProjectTagVocabulary()
    {
        // A per-create unique tag (e.g. one carrying the StagedIdentity) mints one NEW
        // project-wide tag per published item, forever — unbounded against ADO's ~5,000
        // unique-tag project cap, and it writes twig's private bookkeeping into a namespace
        // every human in the project sees. 0001 §1: the shared substrate is ADO, and twig owns
        // only the pending set.
        //
        // Whatever the tag is, it must not vary per seed. This asserts that directly.
        var forOneSeed = PublishIntent.IntentTag;
        var forAnotherSeed = PublishIntent.IntentTag;

        forOneSeed.ShouldBe(forAnotherSeed);
        forOneSeed.ShouldNotContain(_identity.ToString());
        forOneSeed.ShouldNotContain(StagedIdentity.New().ToString());
    }

    [Fact]
    public void IntentTag_AvoidsCharactersAdoRejectsOrMisreads()
    {
        var tag = PublishIntent.IntentTag;

        // ADO reads a leading '@' as a query macro, which makes the tag unqueryable — and an
        // unqueryable tag cannot answer "did my create already happen?".
        tag.ShouldNotStartWith("@");

        // ';' and ',' are tag separators: either would split one tag into two.
        tag.ShouldNotContain(";");
        tag.ShouldNotContain(",");

        // ADO caps tags at 400 characters.
        tag.Length.ShouldBeLessThan(400);
    }
}
