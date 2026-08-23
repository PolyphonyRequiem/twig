using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Focused tests for the review blockers on <see cref="PlanOperationExecutor"/> and its
/// <see cref="PlanSeedPublisher"/> seed helper: canonical batch-field resolution,
/// deterministic strict-CAS relation failure, seed alias-to-identity drift, publish
/// intent/map recovery agreement, and seed link warning classification. Each fake is
/// stubbed to exercise exactly one classification so a regression fails one test rather
/// than a big integration matrix.
/// </summary>
public sealed class PlanOperationExecutorTests
{
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly IWorkItemRepository _workItems = Substitute.For<IWorkItemRepository>();
    private readonly ISeedLinkRepository _seedLinks = Substitute.For<ISeedLinkRepository>();
    private readonly IStagedIdentityRegistry _stagedRegistry = Substitute.For<IStagedIdentityRegistry>();
    private readonly IPublishIdMapRepository _publishIdMap = Substitute.For<IPublishIdMapRepository>();
    private readonly IPublishIntentRepository _publishIntent = Substitute.For<IPublishIntentRepository>();
    private readonly PlanOperationExecutor _executor;
    private readonly List<int> _publishInvocations = new();
    private Func<int, SeedPublishResult> _publishBehaviour = _ => new SeedPublishResult
    {
        Status = SeedPublishStatus.Error,
        ErrorMessage = "PlanSeedPublisher invoked publish unexpectedly.",
    };

    public PlanOperationExecutorTests()
    {
        // Baseline stubs for the collaborators the seed publisher ALWAYS touches, so tests
        // opt-in to overrides for the invariants they exercise. NSubstitute's default
        // Task<IReadOnly...> return is a null-wrapping task; every reachable path here calls
        // GetAllMappingsAsync (via SeedFingerprintCalculator) or GetLinksForItemAsync, so a
        // null return there is uncaught nullref inside the calculator, not a test signal.
        _publishIdMap.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PublishMapping>)Array.Empty<PublishMapping>());
        _seedLinks.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)Array.Empty<SeedLink>());

        var publisher = new PlanSeedPublisher(
            _ado, _workItems, _seedLinks, _stagedRegistry, _publishIdMap, _publishIntent,
            (seedId, _) =>
            {
                _publishInvocations.Add(seedId);
                return Task.FromResult(_publishBehaviour(seedId));
            });
        _executor = new PlanOperationExecutor(_ado, _revisionBound, publisher);
    }

    // ── batch readback: canonical fields ───────────────────────────────────

    [Fact]
    public async Task ReadbackBatch_StateResolvedFromProperty_WhenFieldsMapMissing()
    {
        // The ADO response mapper is authoritative on canonical core fields; the arbitrary
        // Fields dictionary is a mirror the readback should never depend on. If the mapper
        // populated State but not Fields, the batch must still verify.
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 42,
            ExpectedRevision = 3,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Active" },
        };
        var wi = new WorkItem { Id = 42, Title = "T" };
        wi.MarkSynced(4);
        wi.ChangeState("Active"); // sets property; ImportFields is NOT called → Fields dict empty
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackBatch_NullExpected_VerifiesAbsentAndEmpty()
    {
        // A plan value of null asks ADO to clear the field. Both absence AND empty string
        // are legitimate representations of "cleared" — either way the readback verifies.
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?>
            {
                ["System.AssignedTo"] = null, // absent from wi
                ["Custom.Reviewer"] = null,   // present but empty
            },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.UpdateField("Custom.Reviewer", string.Empty);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackBatch_NullExpected_FailsWhenValuePresent()
    {
        var op = new BatchOperation
        {
            Id = "b",
            WorkItemId = 1,
            ExpectedRevision = 1,
            Fields = new Dictionary<string, string?> { ["System.State"] = null },
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(2);
        wi.ChangeState("Active");
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Error!.ShouldContain("cleared");
    }

    // ── link readback: friendly-relation normalization ─────────────────────

    [Fact]
    public async Task ReadbackAddLink_MatchesWhenAdoReturnsFriendlyLinkType()
    {
        // Production AdoResponseMapper normalises non-hierarchy relations to friendly
        // short names ("Successor"). The plan carries "successor". Case-insensitive
        // ordinal match already handles this identity — the test pins it.
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(3);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[] { new WorkItemLink(1, 9, "Successor") }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackAddLink_MatchesWhenAdoReturnsRawAdoRelation()
    {
        // Some paths still surface the raw ADO relation reference name. The readback
        // must recognise both forms and NOT report the edge missing.
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(3);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[]
            {
                new WorkItemLink(1, 9, "System.LinkTypes.Dependency-Forward"),
            }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
    }

    // ── strict-CAS relation not found ──────────────────────────────────────

    [Fact]
    public async Task ExecuteRemoveLink_MissingRelation_IsDeterministicFailure()
    {
        // Strict-CAS remove refuses when the exact (rel, target) is not present at the
        // expected revision. That is a plan-shape violation, not an ambient uncertainty:
        // no readback resurrects a link the server said did not exist. Mapping through
        // AdoException would leak as Indeterminate — the specialised exception fixes it.
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        _revisionBound.RemoveLinkAtRevisionAsync(1, "System.LinkTypes.Dependency-Forward", 9, 2,
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoRelationNotFoundException(
                1, "System.LinkTypes.Dependency-Forward", 9, 2));

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("not present");
    }

    [Fact]
    public async Task ExecuteRemoveLink_MissingParent_IsDeterministicFailure()
    {
        // Unparent-of-nothing rides the same seam and MUST be determinate. Parent maps to
        // Hierarchy-Reverse; the strict-CAS surface throws the same exception.
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 5, ExpectedRevision = 2, Relation = "parent",
        };
        _revisionBound.RemoveLinkAtRevisionAsync(1, "System.LinkTypes.Hierarchy-Reverse", 5, 2,
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoRelationNotFoundException(
                1, "System.LinkTypes.Hierarchy-Reverse", 5, 2));

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
    }

    // ── seed publish: cached identity drift ────────────────────────────────

    [Fact]
    public async Task ExecutePublishSeed_CachedIdentityMismatch_FailsBeforeFingerprint()
    {
        // A cache rebuild reissued the alias to a different staged identity than the plan
        // named. The fingerprint below could still coincidentally match; refuse on the
        // identity mismatch itself so a stale plan cannot publish the wrong seed.
        var planned = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000101"));
        var other = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000102"));
        var alias = MakeAlias(-42);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = planned, ExpectedFingerprint = "irrelevant",
        };

        _publishIdMap.GetNewIdAsync(planned, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(planned, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        _stagedRegistry.FindAliasAsync(planned, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value,
                Title = "seed",
                Type = WorkItemType.Parse("Task").Value,
                IsSeed = true,
                StagedIdentity = other, // <-- drift
            });

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("identity");
    }

    // ── seed publish: fresh Confirmed fingerprint-first ordering ──────────

    [Fact]
    public async Task ExecutePublishSeed_ExternalPrepublishAfterEdit_FailsClosed()
    {
        // The seed drifted locally after the plan was captured and a map row already
        // exists for this identity — an external publish, or a stale MappedPublish that no
        // longer describes the seed the plan named. Under the old check-map-first ordering
        // the executor would MappedPublish onto that row; the new ordering computes the
        // fingerprint over the current cache FIRST and fails closed on the drift.
        var planned = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000401"));
        var alias = MakeAlias(-42);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = planned, ExpectedFingerprint = "captured-at-plan-time",
        };
        _stagedRegistry.FindAliasAsync(planned, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value,
                Title = "edited-after-plan",
                Type = WorkItemType.Parse("Task").Value,
                IsSeed = true,
                StagedIdentity = planned,
            });
        // A map row that would previously have been ratified without any fingerprint check.
        _publishIdMap.GetNewIdAsync(planned, Arg.Any<CancellationToken>()).Returns(9999);
        _publishIntent.GetIntentAsync(planned, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);

        var result = await _executor.ExecuteAsync(op, CancellationToken.None);

        result.Outcome.ShouldBe(PlanExecutionOutcome.Failed);
        result.Error!.ShouldContain("drift");
        // The map lookup MUST NOT short-circuit before fingerprint attestation.
        result.MappedPublishId.ShouldBeNull();
        _publishInvocations.ShouldBeEmpty();
    }


    // ── seed publish readback: intent/map recovery agreement ───────────────

    [Fact]
    public async Task ReadbackPublishSeed_MapAndIntentAgree_VerifiesRemote()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000201"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 1234,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        var wi = new WorkItem { Id = 1234, Title = "T" };
        wi.MarkSynced(1);
        _ado.FetchWithLinksAsync(1234, Arg.Any<CancellationToken>())
            .Returns((wi, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackPublishSeed_MapAndIntentDisagree_FailsDeterministically()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000202"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 9999,
                CompletedAt = DateTimeOffset.UtcNow,
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeTrue();
        outcome.Error!.ShouldContain("disagree");
    }

    [Fact]
    public async Task ReadbackPublishSeed_IntentOnly_InvokesOrchestratorAndDoesNotDuplicate()
    {
        // Crash between wire (step 7) and local UoW (step 10): the intent completed with a
        // real ADO id but the id map never landed. Recovery re-drives the orchestrator —
        // the completed intent forces step 7 to skip CreateAsync (idempotent by contract),
        // and step 10a lands the missed map row. The readback then verifies remote against
        // that recovered map.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000203"));
        var alias = MakeAlias(-7);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var seed = new WorkItem
        {
            Id = alias.Value, Title = "T", Type = WorkItemType.Parse("Task").Value,
            IsSeed = true, StagedIdentity = identity,
        };
        seed.MarkSynced(1);
        var publishedId = 7777;

        int? mapReturn = null;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>())
            .Returns(_ => mapReturn);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = publishedId,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>()).Returns(seed);
        _publishBehaviour = seedId =>
        {
            // Emulates the orchestrator's step-7 idempotent branch: an intent already
            // records the wire outcome, no CreateAsync is issued, and step 10a records
            // the map. Flip the map return so the follow-up readback sees the row.
            mapReturn = publishedId;
            return new SeedPublishResult
            {
                OldId = seedId, NewId = publishedId, Title = "T",
                Status = SeedPublishStatus.Created,
                LinkWarnings = Array.Empty<string>(),
            };
        };
        var remote = new WorkItem { Id = publishedId, Title = "T" };
        remote.MarkSynced(2);
        _ado.FetchWithLinksAsync(publishedId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        _publishInvocations.ShouldBe(new[] { alias.Value });
        // No wire-level create was issued through this test's ADO surface — the recovery
        // path never bypasses the orchestrator into a fresh CreateAsync.
        await _ado.DidNotReceive().CreateAsync(
            Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadbackPublishSeed_IntentOnly_RecoveryFailsWithoutMapRow_IsIndeterminate()
    {
        // Recovery ran but the orchestrator returned success without recording a map row
        // (rollback inside step 10 with no #270 fix, or a stub-shaped result). We keep
        // Indeterminate rather than Verified — the local commit is the proof this readback
        // needs before it can claim the outcome.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000206"));
        var alias = MakeAlias(-8);
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 4242,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(new WorkItem
            {
                Id = alias.Value, Title = "T", Type = WorkItemType.Parse("Task").Value,
                IsSeed = true, StagedIdentity = identity,
            });
        _publishBehaviour = seedId => new SeedPublishResult
        {
            OldId = seedId, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = Array.Empty<string>(),
        };

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("id map");
    }

    [Fact]
    public async Task ReadbackPublishSeed_NoIntentAndNoMap_IsIndeterminate()
    {
        // Neither ledger records an outcome and the apply carried no MappedPublish id.
        // Nothing local proves the wire was touched; the readback cannot claim Verified
        // and cannot deterministically fail either.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000205"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("evidence");
        _publishInvocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadbackPublishSeed_MapPresentRemoteMissing_IsIndeterminate()
    {
        // The map recorded a new id but ADO says 404 — this cannot be a determinate
        // failure (the map remains a valid local commit) but Verified is unreachable
        // until the remote catches up.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000204"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(4242);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        _ado.FetchWithLinksAsync(4242, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(4242));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
    }

    // ── seed publish readback: graph verification ──────────────────────────

    [Fact]
    public async Task ReadbackPublishSeed_MissingRemoteNonHierarchyLink_IsIndeterminate()
    {
        // The item exists on ADO but a promoted non-hierarchy relation the local seed
        // still names is absent from its remote edges. Marking Verified on the mere
        // existence of the id would silently ratify a broken graph — Indeterminate makes
        // the reconcile pass name the missing edge.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000601"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var targetId = 5555;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = newId, Title = "T" };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, targetId, SeedLinkTypes.Successor, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain(SeedLinkTypes.Successor);
        outcome.Error!.ShouldContain("missing");
    }

    [Fact]
    public async Task ReadbackPublishSeed_MissingRemoteParent_IsIndeterminate()
    {
        // parent-child is set at CREATE time (Hierarchy-Reverse), not by the promoter.
        // Verification reads the remote item's ParentId — a divergence there is the same
        // broken-graph classification as a missing non-hierarchy edge.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000603"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var parentId = 3333;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        // Remote has no ParentId.
        var remote = new WorkItem { Id = newId, Title = "T" };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, parentId, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeFalse();
        outcome.Deterministic.ShouldBeFalse();
        outcome.Error!.ShouldContain("parent");
    }

    [Fact]
    public async Task ReadbackPublishSeed_CompleteGraphAcrossParentAndNonHierarchyRelations_Verifies()
    {
        // Happy path: fetched item reflects every intended promoted edge — parent via
        // ParentId, non-hierarchy via WorkItemLink surfaced in the friendly short-name
        // form. The readback verifies once ALL are covered.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000602"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        var newId = 4242;
        var parentId = 3333;
        var successorId = 5555;
        var relatedId = 6666;
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(newId);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = newId, Title = "T", ParentId = parentId };
        remote.MarkSynced(3);
        _ado.FetchWithLinksAsync(newId, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)new[]
            {
                new WorkItemLink(newId, successorId, "Successor"),
                new WorkItemLink(newId, relatedId, "System.LinkTypes.Related"), // raw form
            }));
        _seedLinks.GetLinksForItemAsync(newId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)new[]
            {
                new SeedLink(newId, parentId, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
                new SeedLink(newId, successorId, SeedLinkTypes.Successor, DateTimeOffset.UtcNow),
                new SeedLink(newId, relatedId, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            });

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.Deterministic.ShouldBeTrue();
    }

    // ── seed publish link warnings classification ──────────────────────────

    [Fact]
    public void ClassifySeedPublishSuccess_CacheOnlyWarning_StaysApplied()
    {
        // A "relationship cache refresh failed" note is cosmetic — the remote work item
        // and its edges already reflect the intent; only the local cache mirror needs a
        // follow-up sync. The publish is Applied and the readback promotes to Verified.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000301"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Work item #4242 was published, but relationship cache refresh failed: db locked",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_RemoteLinkFailure_IsIndeterminate()
    {
        // A "Failed to create ADO link ..." warning is a remote link-promotion failure:
        // the item exists but a promised edge is missing. Applied → Verified would
        // silently ratify a broken graph; we surface Indeterminate so the reconcile
        // pass names the missing edge before Verified is possible.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000302"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Failed to create ADO link (Successor) between 4242 and 5555: server 500.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
        classified.Error!.ShouldContain("link");
    }

    [Fact]
    public void ClassifySeedPublishSuccess_UnknownLinkType_IsIndeterminate()
    {
        // An unmapped seed link type is a link-promotion failure too: the local seed
        // named an edge the promoter cannot land on ADO. Same classification.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000303"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Unknown link type 'MysteryEdge' between 4242 and 5555; skipped.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_MixedWarnings_TakesFirstRemoteAsIndeterminate()
    {
        // Even one non-cache warning downgrades the whole result — the reviewer flagged
        // "never ignore link-promotion failure" as an invariant.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000304"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = new[]
            {
                "Work item #4242 was published, but relationship cache refresh failed: harmless.",
                "Failed to create ADO link (Related) between 4242 and 5555: 502 Bad Gateway.",
            },
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Indeterminate);
    }

    [Fact]
    public void ClassifySeedPublishSuccess_NoWarnings_IsApplied()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000305"));
        var result = new SeedPublishResult
        {
            OldId = -1, NewId = 4242, Title = "T",
            Status = SeedPublishStatus.Created,
            LinkWarnings = Array.Empty<string>(),
        };

        var classified = PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity);

        classified.Outcome.ShouldBe(PlanExecutionOutcome.Applied);
        classified.ResultJson!.ShouldContain("4242");
    }

    // ── canonical readback ResultJson for recovered-Verified rows ─────────
    //
    // Every readback that proves a recovered operation Verified MUST carry the
    // canonical ResultJson the lifecycle threads into the atomic Applying→Applied
    // record. A recovered Verified row with a NULL result_json would silently break
    // CLI/MCP status which reads the raw column.

    [Fact]
    public async Task ReadbackBatch_Verified_CarriesCurrentServerRevision()
    {
        var op = new BatchOperation
        {
            Id = "b", WorkItemId = 42, ExpectedRevision = 3,
            Fields = new Dictionary<string, string?> { ["System.State"] = "Active" },
        };
        var wi = new WorkItem { Id = 42, Title = "T" };
        wi.MarkSynced(7);
        wi.ChangeState("Active");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":7}");
    }

    [Fact]
    public async Task ReadbackAddLink_Parent_Verified_CarriesCurrentServerRevision()
    {
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 5, ExpectedRevision = 2, Relation = "parent",
        };
        var wi = new WorkItem { Id = 1, Title = "T", ParentId = 5 };
        wi.MarkSynced(11);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(wi);

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":11}");
    }

    [Fact]
    public async Task ReadbackAddLink_NonParent_Verified_CarriesCurrentServerRevision()
    {
        var op = new AddLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(13);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)new[] { new WorkItemLink(1, 9, "Successor") }));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":13}");
    }

    [Fact]
    public async Task ReadbackRemoveLink_Verified_CarriesCurrentServerRevision()
    {
        var op = new RemoveLinkOperation
        {
            Id = "L", WorkItemId = 1, OtherId = 9, ExpectedRevision = 2, Relation = "successor",
        };
        var wi = new WorkItem { Id = 1, Title = "T" };
        wi.MarkSynced(17);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(
            (wi, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"revision\":17}");
    }

    [Fact]
    public async Task ReadbackDelete_NotFound_Verified_CarriesDeletedMarker()
    {
        var op = new DeleteOperation { Id = "D", WorkItemId = 5, ExpectedRevision = 6 };
        _ado.FetchAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(5));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldBe("{\"deleted\":true}");
    }

    [Fact]
    public async Task ReadbackPublishSeed_MappedVerifies_CarriesIdentityAndPublishedId()
    {
        // The mapped-seed crash: executor already MappedPublish'd this row, the process
        // crashed mid-Applying, and recovery finds the map row still there. The readback
        // is what will settle the recovered Verified row's result — it MUST carry the
        // canonical {"identity":<planned>,"publishedId":<map>} shape.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-0000000009a1"));
        var op = new PublishSeedOperation
        {
            Id = "S", StagedIdentity = identity, ExpectedFingerprint = "x",
        };
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(4242);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);
        var remote = new WorkItem { Id = 4242, Title = "T" };
        remote.MarkSynced(1);
        _ado.FetchWithLinksAsync(4242, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var outcome = await _executor.ReadbackAsync(op, default, CancellationToken.None);

        outcome.Ok.ShouldBeTrue();
        outcome.ResultJson.ShouldNotBeNull();
        outcome.ResultJson.ShouldBe($"{{\"identity\":\"{identity}\",\"publishedId\":4242}}");
    }

    private static StagedAlias MakeAlias(int negative)
    {
        StagedAlias.TryFrom(negative, out var alias).ShouldBeTrue();
        return alias;
    }
}
