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
/// These cover the <b>7→10 window</b>: the ADO create happens outside the transaction that
/// rolls back at step 10, so a crash in between orphans a real work item with no local trace,
/// and every retry creates another duplicate (PolyphonyRequiem/twig#270).
/// </para>
/// <para>
/// <b>The intent ledger is a STATEFUL FAKE, not a mock.</b> Review found the original mock made
/// the recovery tests vacuous: it returned a constant <c>RecordedAt</c> and never persisted an
/// outcome, so the assertions passed against code that could not actually recover. Recovery is
/// a property of state surviving between two calls, so the double has to hold state — this fake
/// mirrors <c>SqlitePublishIntentRepository</c>'s contract, including the rule that an existing
/// intent is returned as-is rather than overwritten.
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
    private readonly FakeIntentLedger _intentRepo = new();

    private readonly SeedPublishOrchestrator _orchestrator;
    private readonly StagedIdentity _identity = StagedIdentity.New();

    public SeedPublishIntentTests()
    {
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);

        // Default: "ADO has no matching in-flight item" — the ordinary first-attempt path.
        _adoService.FindPublishedIntentAsync(Arg.Any<PublishIntent>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        // Registered ONCE; the flag decides whether it throws. See _localTransactionFails.
        _workItemRepo
            .When(r => r.DeleteByIdAsync(-1, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (_localTransactionFails)
                    throw new InvalidOperationException("FOREIGN KEY constraint failed");
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
            pendingChangeStore: Substitute.For<IPendingChangeStore>(),
            publishIntentRepo: _intentRepo,
            sprintEntryPolicy: ReferenceProfileBuilder.SprintPolicy());
    }

    private WorkItem ArrangeSeed()
    {
        var seed = new WorkItemBuilder(-1, "A staged seed")
            .AsTask()
            .AsSeed(stagedIdentity: _identity)
            .Build();

        // Fixture guard: the identity is what keys the intent. Without it the orchestrator takes
        // the pre-0014 unprotected path and every assertion below would pass vacuously.
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

    // NSubstitute ACCUMULATES When…Do callbacks — registering a second, empty one does NOT
    // replace a throwing one, so the throw would still fire on the retry and the test would fail
    // for a reason unrelated to what it asserts. A flag the single callback reads is the
    // unambiguous way to arm and disarm it.
    private bool _localTransactionFails;

    /// <summary>Makes the step-10 transaction fail, reproducing the #270 rollback.</summary>
    private void MakeLocalTransactionFail() => _localTransactionFails = true;

    /// <summary>Lets the retry commit.</summary>
    private void MakeLocalTransactionSucceed() => _localTransactionFails = false;

    // ═══════════════════════════════════════════════════════════════
    //  THE BUG (#270): a retry after a rolled-back first attempt must
    //  ADOPT the orphan, never create a second one
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SecondPublishAfterARolledBackFirst_CreatesInAdoExactlyOnce()
    {
        ArrangeSeed();

        // ── Attempt 1: the ADO create lands, then step 10 rolls back. This is #270 exactly.
        MakeLocalTransactionFail();
        await Should.ThrowAsync<InvalidOperationException>(() => _orchestrator.PublishAsync(-1));

        await _adoService.Received(1).CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());

        // Fixture guard: prove the rollback actually happened, or "no duplicate" is meaningless.
        await _unitOfWork.Received(1).RollbackAsync(_transaction, Arg.Any<CancellationToken>());

        // ── Attempt 2: the retry. It must find the orphan, not make a new one.
        MakeLocalTransactionSucceed();
        var result = await _orchestrator.PublishAsync(-1);

        // THE ASSERTION THAT MATTERS. Still ONE create across BOTH attempts.
        await _adoService.Received(1).CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
    }

    [Fact]
    public async Task SecondPublishAfterARolledBackFirst_AdoptsTheOrphanFromTheLedger()
    {
        ArrangeSeed();

        MakeLocalTransactionFail();
        await Should.ThrowAsync<InvalidOperationException>(() => _orchestrator.PublishAsync(-1));

        // The ledger is the surviving evidence: it must still name the ADO id after the rollback.
        var afterFailure = await _intentRepo.GetIntentAsync(_identity);
        afterFailure.ShouldNotBeNull();
        afterFailure.PublishedId.ShouldBe(500, "the completed outcome must survive the rollback");

        // ADO cannot help on the retry — force the tag query to find nothing. The ledger alone
        // has to carry recovery, which is the read path it was built for.
        _adoService.FindPublishedIntentAsync(Arg.Any<PublishIntent>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        MakeLocalTransactionSucceed();
        var result = await _orchestrator.PublishAsync(-1);

        result.NewId.ShouldBe(500);
        await _adoService.Received(1).CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RolledBackAttempt_DoesNotStripTheIntentTag()
    {
        ArrangeSeed();
        MakeLocalTransactionFail();

        await Should.ThrowAsync<InvalidOperationException>(() => _orchestrator.PublishAsync(-1));

        // The tag marks IN-FLIGHT state. Stripping it before the transaction commits disarms the
        // guard for exactly the window it protects: the orphan would carry no tag, so
        // FindPublishedIntentAsync could not narrow to it and the retry would duplicate.
        await _adoService.DidNotReceive().ClearIntentTagAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfulPublish_StripsTheIntentTagOnlyAfterTheCommit()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        Received.InOrder(() =>
        {
            _unitOfWork.CommitAsync(_transaction, Arg.Any<CancellationToken>());
            _adoService.ClearIntentTagAsync(500, Arg.Any<CancellationToken>());
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Intent is recorded BEFORE the ADO call, outcome after it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_RecordsIntentBeforeCreatingInAdo()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        _intentRepo.Calls.IndexOf($"record:{_identity}")
            .ShouldBeLessThan(_intentRepo.Calls.IndexOf($"complete:{_identity}:500"));
        _intentRepo.Calls[0].ShouldStartWith("record:");
    }

    [Fact]
    public async Task PublishAsync_RecordsOutcomeAfterCreateSucceeds()
    {
        ArrangeSeed();

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        var stored = await _intentRepo.GetIntentAsync(_identity);
        stored.ShouldNotBeNull();
        stored.PublishedId.ShouldBe(500);
        stored.IsOpen.ShouldBeFalse();
    }

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
    public async Task PublishAsync_WhenClearingTheTagFails_ThePublishStillSucceeds()
    {
        ArrangeSeed();
        _adoService
            .When(a => a.ClearIntentTagAsync(500, Arg.Any<CancellationToken>()))
            .Do(_ => throw new HttpRequestException("ADO unreachable"));

        var result = await _orchestrator.PublishAsync(-1);

        // The publish has already succeeded by then; a stale cosmetic tag must not turn it into
        // a reported failure. The ledger still names the id, so recovery does not depend on it.
        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
    }

    [Fact]
    public async Task PublishAsync_WhenAdoAlreadyHasTheItem_DoesNotCreateADuplicate()
    {
        ArrangeSeed();

        // No ledger row (e.g. a crash before the outcome was recorded), but ADO holds the item.
        _adoService.FindPublishedIntentAsync(Arg.Any<PublishIntent>(), Arg.Any<CancellationToken>())
            .Returns(500);

        var result = await _orchestrator.PublishAsync(-1);

        await _adoService.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        result.NewId.ShouldBe(500);
    }

    [Fact]
    public async Task PublishAsync_QueriesAdoWithTheIntentItRecorded()
    {
        ArrangeSeed();

        await _orchestrator.PublishAsync(-1);

        // The fence must be the intent's OWN RecordedAt — a fence from anywhere else is not a
        // guaranteed lower bound on the create it is meant to find.
        var recorded = _intentRepo.Recorded[_identity];
        await _adoService.Received(1).FindPublishedIntentAsync(
            Arg.Is<PublishIntent>(i =>
                i.Title == "A staged seed" && i.RecordedAt == recorded.RecordedAt),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tag CARDINALITY — the project tag vocabulary is SHARED
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IntentTag_IsAConstant_SoPublishingDoesNotGrowTheProjectTagVocabulary()
    {
        // A per-create unique tag mints one NEW project-wide tag per published item, forever —
        // unbounded against ADO's ~5,000 unique-tag project cap, and it writes twig's private
        // bookkeeping into a namespace every human in the project sees. 0001 §1: the shared
        // substrate is ADO, and twig owns only the pending set.
        PublishIntent.IntentTag.ShouldNotContain(_identity.ToString());
        PublishIntent.IntentTag.ShouldNotContain(StagedIdentity.New().ToString());
    }

    [Fact]
    public void IntentTag_AvoidsCharactersAdoRejectsOrMisreads()
    {
        var tag = PublishIntent.IntentTag;

        // ADO reads a leading '@' as a query macro, making the tag unqueryable — and an
        // unqueryable tag cannot answer "did my create already happen?".
        tag.ShouldNotStartWith("@");

        // ';' and ',' are tag separators: either would split one tag into two.
        tag.ShouldNotContain(";");
        tag.ShouldNotContain(",");

        // ADO caps tags at 400 characters.
        tag.Length.ShouldBeLessThan(400);
    }

    /// <summary>
    /// A stateful stand-in for <c>SqlitePublishIntentRepository</c>. Mirrors the one contract
    /// rule recovery depends on: an EXISTING intent — open or completed — is returned as-is,
    /// never overwritten, so neither <c>RecordedAt</c> nor a recorded <c>PublishedId</c> is lost
    /// on a retry.
    /// </summary>
    private sealed class FakeIntentLedger : IPublishIntentRepository
    {
        private readonly Dictionary<StagedIdentity, PublishIntent> _rows = [];

        public List<string> Calls { get; } = [];

        public IReadOnlyDictionary<StagedIdentity, PublishIntent> Recorded => _rows;

        public Task<PublishIntent> RecordIntentAsync(
            StagedIdentity identity, string title, string typeName, CancellationToken ct = default)
        {
            Calls.Add($"record:{identity}");

            if (_rows.TryGetValue(identity, out var existing))
                return Task.FromResult(existing);

            var intent = new PublishIntent
            {
                Identity = identity,
                Title = title,
                TypeName = typeName,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            _rows[identity] = intent;
            return Task.FromResult(intent);
        }

        public Task CompleteIntentAsync(
            StagedIdentity identity, int publishedId, CancellationToken ct = default)
        {
            Calls.Add($"complete:{identity}:{publishedId}");

            if (_rows.TryGetValue(identity, out var existing))
            {
                _rows[identity] = existing with
                {
                    PublishedId = publishedId,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
            }

            return Task.CompletedTask;
        }

        public Task<PublishIntent?> GetIntentAsync(
            StagedIdentity identity, CancellationToken ct = default)
            => Task.FromResult(_rows.TryGetValue(identity, out var i) ? i : null);

        public Task<IReadOnlyList<PublishIntent>> GetOpenIntentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PublishIntent>>(
                _rows.Values.Where(i => i.IsOpen).ToList());
    }
}
