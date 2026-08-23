using System.Net.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

/// <summary>
/// Focused tests for <see cref="PlanLifecycleService"/> — the ONE service every plan-touching
/// surface routes through. Failure modes fake the collaborators so they exercise exactly the
/// classification the service owns, not the SQLite persistence or the ADO wire.
/// </summary>
public sealed class PlanLifecycleServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly SqliteCacheStore _store;
    private readonly SqlitePlanJournalRepository _journal;
    private readonly IPendingChangeReader _pending = Substitute.For<IPendingChangeReader>();
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly IWorkItemRepository _workItems = Substitute.For<IWorkItemRepository>();
    private readonly ISeedLinkRepository _seedLinks = Substitute.For<ISeedLinkRepository>();
    private readonly IStagedIdentityRegistry _stagedRegistry = Substitute.For<IStagedIdentityRegistry>();
    private readonly IPublishIdMapRepository _publishIdMap = Substitute.For<IPublishIdMapRepository>();
    private readonly IPublishIntentRepository _publishIntent = Substitute.For<IPublishIntentRepository>();
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly FakeSeedPublish _seedPublish = new();

    public PlanLifecycleServiceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"twig-plan-lc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        var twigDir = Path.Combine(_repoRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        _store = new SqliteCacheStore("Data Source=:memory:");
        _journal = new SqlitePlanJournalRepository(_store);
        _config = new TwigConfiguration();
        _config.Organization = "acme";
        _config.Project = "cache";
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"),
            Path.Combine(twigDir, "twig.db"), _repoRoot);

        _pending.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingChangeDetail>>(Array.Empty<PendingChangeDetail>()));
        _publishIdMap.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PublishMapping>>(Array.Empty<PublishMapping>()));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { }
    }

    private PlanLifecycleService BuildService(DateTimeOffset? clock = null)
    {
        var provider = clock is null
            ? TimeProvider.System
            : (TimeProvider)new FakeClock(clock.Value);
        return new PlanLifecycleService(
            new PlanDocumentParser(),
            _journal,
            _pending,
            _ado,
            _revisionBound,
            _seedPublish.Orchestrator,
            _workItems,
            _seedLinks,
            _stagedRegistry,
            _publishIdMap,
            _publishIntent,
            _config,
            _paths,
            provider);
    }

    // ── path guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_RefusesFileOutsideWorkspaceRoot()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"twig-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var file = Path.Combine(outsideDir, "plan.json");
            await File.WriteAllTextAsync(file, ValidPlanSource());
            var svc = BuildService();

            var result = await svc.ValidateAsync(file);

            result.IsValid.ShouldBeFalse();
            result.Issues.ShouldContain(i => i.Message.Contains("outside the workspace root"));
        }
        finally { Directory.Delete(outsideDir, recursive: true); }
    }

    [Fact]
    public async Task Validate_RefusesSiblingPrefixCollision()
    {
        // Segment-aware containment: /repo/.twig-evil is not inside /repo/.twig even though
        // one string is a prefix of the other. The lifecycle refuses.
        var neighbour = _repoRoot + "-evil";
        Directory.CreateDirectory(neighbour);
        try
        {
            var file = Path.Combine(neighbour, "plan.json");
            await File.WriteAllTextAsync(file, ValidPlanSource());
            var svc = BuildService();

            var result = await svc.ValidateAsync(file);

            result.IsValid.ShouldBeFalse();
            result.Issues.ShouldContain(i => i.Message.Contains("outside the workspace root"));
        }
        finally { Directory.Delete(neighbour, recursive: true); }
    }

    [Fact]
    public async Task Validate_RejectsWorkspaceMismatch_CaseInsensitive()
    {
        // Case-insensitive equality: "ACME"/"CACHE" matches "acme"/"cache". But the same
        // check MUST reject "acme"/"other" as a mismatch.
        var okFile = WritePlan(ValidPlanSource(org: "ACME", project: "CACHE"));
        var badFile = WritePlan(ValidPlanSource(org: "acme", project: "other"), "bad.json");
        var svc = BuildService();

        (await svc.ValidateAsync(okFile)).IsValid.ShouldBeTrue();

        var bad = await svc.ValidateAsync(badFile);
        bad.IsValid.ShouldBeFalse();
        bad.Issues.ShouldContain(i => i.Message.Contains("does not match active workspace"));
    }

    // ── preview: pending blocker + import ──────────────────────────────────

    [Fact]
    public async Task Preview_ImportsJournalAndReturnsPendingSnapshot()
    {
        var file = WritePlan(ValidPlanSource());
        var pending = new[]
        {
            new PendingChangeDetail(1, 42, "note", null, "hello", null, "hello", DateTimeOffset.UtcNow, null),
        };
        _pending.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingChangeDetail>>(pending));

        var result = await BuildService().PreviewAsync(file);

        result.Digest.ShouldNotBeNullOrEmpty();
        result.PendingChanges.ShouldBe(pending);
        result.CanApply.ShouldBeFalse();

        // Journal was imported.
        (await _journal.GetAsync(result.Digest!)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Preview_ZeroPending_CanApplyTrue()
    {
        var file = WritePlan(ValidPlanSource());
        var result = await BuildService().PreviewAsync(file);

        result.CanApply.ShouldBeTrue();
        result.PendingChanges.ShouldBeEmpty();
    }

    // ── apply: hard preconditions ──────────────────────────────────────────

    [Fact]
    public async Task Apply_RefusesOnDigestMismatch_AndDoesNotWriteJournal()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var apply = await svc.ApplyAsync(file, "0000000000000000000000000000000000000000000000000000000000000000");

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("does not match confirmed digest");
        var journalAfter = await _journal.GetAsync(digest);
        journalAfter!.State.ShouldBe(PlanOperationState.Planned); // still not confirmed
    }

    [Fact]
    public async Task Apply_RefusesWhenPendingRowsPresent()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Now pending rows exist at apply time.
        _pending.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingChangeDetail>>(new[]
            {
                new PendingChangeDetail(1, 42, "note", null, "x", null, "x", DateTimeOffset.UtcNow, null),
            }));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("pending change");
    }

    [Fact]
    public async Task Apply_RefusesWithoutPreviousPreviewJournal()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        // Compute the digest without importing the journal.
        var digest = (await svc.ValidateAsync(file)).Digest!;

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("No preview journal");
    }

    // ── apply: happy path (batch) ──────────────────────────────────────────

    [Fact]
    public async Task Apply_Batch_VerifiedThroughReadback()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 4,
            state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        var journalRow = (await _journal.GetAsync(digest))!;
        journalRow.State.ShouldBe(PlanOperationState.Verified);
    }

    // ── apply: CAS conflict (batch) ─────────────────────────────────────────

    [Fact]
    public async Task Apply_Batch_CasConflict_MarksFailed_AndTailUntouched()
    {
        var file = WritePlan(TwoBatchPlan(a: (100, 3), b: (101, 5)));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoConflictException(4, "test"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[0].Error!.ShouldContain("Revision conflict");
        // Second op MUST be left untouched — the plan stops on first non-Verified terminal.
        apply.Operations[1].State.ShouldBe(PlanOperationState.Confirmed);
        // PlanApplyResult.Error is reserved for pre-loop refusals; per-op detail lives on
        // the operation row's Error, not the top-level Error.
        apply.Error.ShouldBeNull();
        await _ado.DidNotReceive().PatchAsync(101, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── apply: link / delete happy paths ────────────────────────────────────

    [Fact]
    public async Task Apply_AddLink_UsesRevisionBoundServiceAndVerifiesViaLinks()
    {
        var file = WritePlan(AddLinkPlan(sourceId: 1, otherId: 9, rev: 2, relation: "successor"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.AddLinkAtRevisionAsync(1, "System.LinkTypes.Dependency-Forward", 9, 2,
                Arg.Any<CancellationToken>()).Returns(3);
        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<
            (WorkItem Item, IReadOnlyList<WorkItemLink> Links)>(
                (BuildWorkItem(1, rev: 3),
                 new[] { new WorkItemLink(1, 9, "System.LinkTypes.Dependency-Forward") })));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
    }

    [Fact]
    public async Task Apply_Delete_VerifiedOn404()
    {
        var file = WritePlan(DeletePlan(workItemId: 5, rev: 6));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.FetchAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(5));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
    }

    // ── crash-state recovery ────────────────────────────────────────────────

    [Fact]
    public async Task Apply_RecoveryFromApplying_ReadsBackWithoutReissuing()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Simulate a crash after Applying was persisted but before the ADO call returned.
        // StartedAt is pushed past the 5-minute Applying lease so this row is treated as a
        // dead-winner recovery target and not a live concurrent apply.
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        // Recovery MUST NOT re-issue the PATCH.
        await _ado.DidNotReceive().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Atomic Applying → Applied stamped applied_at during recovery: an Applied row
        // without applied_at would prove the atomic record wasn't used.
        apply.Operations[0].AppliedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Apply_RecoveryFromApplied_ReadsBackWithoutReissuing()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Applying, PlanOperationState.Applied, DateTimeOffset.UtcNow);

        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── seed publish: fingerprint drift ─────────────────────────────────────

    [Fact]
    public async Task Apply_SeedPublish_FingerprintDrift_FailsBeforeAnyAdoCall()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000001"));
        var alias = MakeAlias(-42);
        var file = WritePlan(PublishSeedPlan(identity, expectedFingerprint: "deadbeef"));

        _stagedRegistry.FindAliasAsync(identity, Arg.Any<CancellationToken>()).Returns(alias);
        var seed = BuildSeed(alias.Value, identity, title: "T", type: "Task");
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>()).Returns(seed);
        _seedLinks.GetLinksForItemAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns((PublishIntent?)null);

        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[0].Error!.ShouldContain("fingerprint drift");
        _seedPublish.CallCount.ShouldBe(0);
    }

    // ── seed publish: map-based recovery (no republish) ────────────────────

    [Fact]
    public async Task Apply_SeedPublish_MapAlreadyRecorded_ReadsBackWithoutRepublish()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000002"));
        var file = WritePlan(PublishSeedPlan(identity, expectedFingerprint: "irrelevant"));

        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 1234,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        var remote = BuildWorkItem(1234, rev: 1);
        _ado.FetchAsync(1234, Arg.Any<CancellationToken>()).Returns(remote);
        _ado.FetchWithLinksAsync(1234, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(1234, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)Array.Empty<SeedLink>());

        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        _seedPublish.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_SeedPublish_IntentMapDisagreement_FailsClosed()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000003"));
        var file = WritePlan(PublishSeedPlan(identity, expectedFingerprint: "x"));

        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(1234);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 9999,
                CompletedAt = DateTimeOffset.UtcNow,
            });

        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[0].Error!.ShouldContain("disagree");
        _seedPublish.CallCount.ShouldBe(0);
    }

    // ── status ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ValidDigestNoJournal_ReturnsNull()
    {
        // The ONE null case: parse succeeded, workspace matches, no journal has ever been
        // imported for this digest.
        var file = WritePlan(ValidPlanSource());
        var result = await BuildService().StatusAsync(file);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Status_AfterPreview_ReturnsPlannedJournal()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        await svc.PreviewAsync(file);

        var status = await svc.StatusAsync(file);
        status.ShouldNotBeNull();
        status!.Found.ShouldBeTrue();
        status.Issues.ShouldBeEmpty();
        status.State.ShouldBe(PlanOperationState.Planned);
        status.Operations.ShouldNotBeEmpty();
        status.Operations.ShouldAllBe(o => o.State == PlanOperationState.Planned);
    }

    [Fact]
    public async Task Status_OutsideWorkspace_ReturnsIssuesFoundFalse()
    {
        // Input error surfaces as a non-null PlanStatusResult with Issues; only "valid
        // digest, no journal" returns null.
        var outsideDir = Path.Combine(Path.GetTempPath(), $"twig-outside-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var file = Path.Combine(outsideDir, "plan.json");
            await File.WriteAllTextAsync(file, ValidPlanSource());

            var status = await BuildService().StatusAsync(file);

            status.ShouldNotBeNull();
            status!.Found.ShouldBeFalse();
            status.State.ShouldBeNull();
            status.Operations.ShouldBeEmpty();
            status.Issues.ShouldContain(i => i.Message.Contains("outside the workspace root"));
        }
        finally { Directory.Delete(outsideDir, recursive: true); }
    }

    [Fact]
    public async Task Status_UnreadableFile_ReturnsIssuesFoundFalse()
    {
        var status = await BuildService().StatusAsync(Path.Combine(_repoRoot, "missing.json"));

        status.ShouldNotBeNull();
        status!.Found.ShouldBeFalse();
        status.Issues.ShouldNotBeEmpty();
        status.Issues[0].Code.ShouldBe(PlanValidationCodes.JsonInvalid);
    }

    [Fact]
    public async Task Status_InvalidJson_ReturnsIssuesFoundFalse()
    {
        var file = WritePlan("{{{not valid json");

        var status = await BuildService().StatusAsync(file);

        status.ShouldNotBeNull();
        status!.Found.ShouldBeFalse();
        status.Digest.ShouldBeNull();
        status.Issues.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Status_WorkspaceMismatch_ReturnsIssuesFoundFalse()
    {
        var file = WritePlan(ValidPlanSource(org: "acme", project: "other"));

        var status = await BuildService().StatusAsync(file);

        status.ShouldNotBeNull();
        status!.Found.ShouldBeFalse();
        // Parser rejects on workspace mismatch and clears the digest.
        status.Digest.ShouldBeNull();
        status.Issues.ShouldContain(i => i.Message.Contains("does not match active workspace"));
    }

    // ── path guard: symlink resolution ─────────────────────────────────────

    [Fact]
    public async Task PathGuard_FollowsSymlinkedFileIntoWorkspace()
    {
        // A symlink INSIDE the workspace root that points at a real file OUTSIDE the root
        // is refused: the final target names the actual bytes we would read, and those
        // bytes are outside the workspace boundary. Refuse.
        if (OperatingSystem.IsWindows()) return; // symlink creation typically needs admin

        var outsideDir = Path.Combine(Path.GetTempPath(), $"twig-sym-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var realFile = Path.Combine(outsideDir, "plan.json");
            await File.WriteAllTextAsync(realFile, ValidPlanSource());

            var linkInsideRepo = Path.Combine(_repoRoot, "linked-plan.json");
            File.CreateSymbolicLink(linkInsideRepo, realFile);

            var status = await BuildService().StatusAsync(linkInsideRepo);

            status.ShouldNotBeNull();
            status!.Found.ShouldBeFalse();
            status.Issues.ShouldContain(i => i.Message.Contains("outside the workspace root"));
        }
        finally { try { Directory.Delete(outsideDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task PathGuard_AcceptsSymlinkedFileThatResolvesInsideWorkspace()
    {
        // The mirror case: a symlink OUTSIDE the workspace whose target is a real file
        // inside is accepted — the final path is what we compare against the root, and
        // that final path lands inside.
        if (OperatingSystem.IsWindows()) return;

        var realFile = WritePlan(ValidPlanSource());
        var linkDir = Path.Combine(Path.GetTempPath(), $"twig-sym-in-{Guid.NewGuid():N}");
        Directory.CreateDirectory(linkDir);
        try
        {
            var linkOutside = Path.Combine(linkDir, "plan.json");
            File.CreateSymbolicLink(linkOutside, realFile);

            var svc = BuildService();
            await svc.PreviewAsync(linkOutside);

            var status = await svc.StatusAsync(linkOutside);
            status.ShouldNotBeNull();
            status!.Found.ShouldBeTrue();
        }
        finally { try { Directory.Delete(linkDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task PathGuard_RefusesFileReachedThroughIntermediateSymlinkedDirectory()
    {
        // Intermediate directory in the path is a symlink whose real target sits OUTSIDE
        // the workspace. Following only the final segment (as File.ResolveLinkTarget does)
        // would miss this and read external bytes. The component-by-component resolver
        // catches the escape.
        if (OperatingSystem.IsWindows()) return;

        var outsideDir = Path.Combine(Path.GetTempPath(), $"twig-sym-mid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var realFile = Path.Combine(outsideDir, "plan.json");
            await File.WriteAllTextAsync(realFile, ValidPlanSource());

            // /repo/gateway -> /tmp/twig-sym-mid-…
            var gateway = Path.Combine(_repoRoot, "gateway");
            Directory.CreateSymbolicLink(gateway, outsideDir);

            // /repo/gateway/plan.json — final segment is a plain file, but the parent
            // directory is a symlink out of the workspace.
            var viaLink = Path.Combine(gateway, "plan.json");

            var status = await BuildService().StatusAsync(viaLink);

            status.ShouldNotBeNull();
            status!.Found.ShouldBeFalse();
            status.Issues.ShouldContain(i => i.Message.Contains("outside the workspace root"));
        }
        finally { try { Directory.Delete(outsideDir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task PathGuard_AcceptsNormalNestedFile()
    {
        // Control case: an ordinary nested file with no symlinks anywhere on the path
        // must be accepted. Guards this component-by-component walker against turning
        // legitimate reads into refusals.
        var nested = Path.Combine(_repoRoot, "plans", "team");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "plan.json");
        await File.WriteAllTextAsync(file, ValidPlanSource());

        var result = await BuildService().ValidateAsync(file);

        result.IsValid.ShouldBeTrue();
        result.Issues.ShouldNotContain(i => i.Message.Contains("outside the workspace root"));
    }

    [Fact]
    public async Task PathGuard_AcceptsSymlinkThatStaysInsideWorkspace()
    {
        // A symlink whose target lives inside the workspace root — both directly and
        // through an intermediate symlink hop — resolves inside and is accepted.
        if (OperatingSystem.IsWindows()) return;

        var realDir = Path.Combine(_repoRoot, "plans");
        Directory.CreateDirectory(realDir);
        var realFile = Path.Combine(realDir, "plan.json");
        await File.WriteAllTextAsync(realFile, ValidPlanSource());

        // /repo/mirror-plans -> /repo/plans, then /repo/current.json -> /repo/mirror-plans/plan.json
        var mirrorDir = Path.Combine(_repoRoot, "mirror-plans");
        Directory.CreateSymbolicLink(mirrorDir, realDir);
        var linkFile = Path.Combine(_repoRoot, "current.json");
        File.CreateSymbolicLink(linkFile, Path.Combine(mirrorDir, "plan.json"));

        var result = await BuildService().ValidateAsync(linkFile);

        result.IsValid.ShouldBeTrue();
        result.Issues.ShouldNotContain(i => i.Message.Contains("outside the workspace root"));
    }

    // ── apply: race / lost CAS ─────────────────────────────────────────────

    [Fact]
    public async Task Apply_RecoveryFromApplying_IndeterminateReadback_TerminalisesWithoutAppliedStamp()
    {
        // Recovery must NOT stamp applied_at on a row whose effect the readback cannot
        // prove — an Applied row without a subsequent Verified would misrepresent an
        // operation that never actually landed. Indeterminate readback → Indeterminate
        // terminal directly.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        // Fetch returns an unchanged revision — readback classifies this as Indeterminate.
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 3, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Indeterminate);
        apply.Operations[0].AppliedAt.ShouldBeNull(); // never stamped Applied
        apply.Operations[0].VerifiedAt.ShouldBeNull();

        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_LostConfirmToApplyingCas_RoutesOffActualPersistedState()
    {
        // Another actor (crash-recovered rerun, concurrent worker) advanced the row past
        // Confirmed before our Confirmed→Applying CAS could fire. The service MUST reload
        // the row, route off the persisted state, and never act as if it had won.
        // Setup: journal exists (Planned). Race: pre-advance the op to Verified so our
        // ApplyAsync's Confirmed→Applying CAS returns false and the reload observes a
        // terminal state — the service must return Verified and never issue an ADO write.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        // Walk the op to Verified out-of-band, simulating a concurrent completion.
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Applying, PlanOperationState.Applied, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Applied, PlanOperationState.Verified, DateTimeOffset.UtcNow);

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        // Never touched ADO: we routed off the persisted terminal state.
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── apply: timestamps ──────────────────────────────────────────────────

    [Fact]
    public async Task Apply_HappyPath_VerifiedAtStampedByAppliedToVerifiedTransitionOnly()
    {
        // verified_at is written by the Applied→Verified CAS exclusively. SaveOperationResult
        // does not stamp it. A monotonic FakeClock lets us prove verified_at > applied_at:
        // if SaveOperationResult had silently written verified_at, the two would be equal
        // (both stamped between the Applied CAS and the Verified CAS), which is the exact
        // regression this test rules out.
        var t0 = DateTimeOffset.Parse("2026-08-22T10:00:00Z");
        var clock = new FakeClock(t0);
        var file = WritePlan(BatchOnlyPlan(workItemId: 1, expectedRev: 1, state: "Active"));
        var svc = BuildService(t0);
        // Rebuild svc against our own advancing clock instance so we can bump time between
        // internal steps.
        var boundClock = clock;
        svc = new PlanLifecycleService(
            new PlanDocumentParser(), _journal, _pending, _ado, _revisionBound,
            _seedPublish.Orchestrator, _workItems, _seedLinks, _stagedRegistry, _publishIdMap,
            _publishIntent, _config, _paths, boundClock);

        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                boundClock.Advance(TimeSpan.FromSeconds(1)); // t+1 after the PATCH returns
                return Task.FromResult(2);
            });
        _ado.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                boundClock.Advance(TimeSpan.FromSeconds(1)); // t+2 after readback fetch
                return Task.FromResult(BuildWorkItem(1, rev: 2, state: "Active"));
            });

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.AppliedAt.ShouldNotBeNull();
        row.VerifiedAt.ShouldNotBeNull();
        // Applied stamp lands BEFORE the readback bumps the clock; Verified stamp lands
        // AFTER. Strict inequality proves verified_at is not being co-written by Save.
        row.VerifiedAt!.Value.ShouldBeGreaterThan(row.AppliedAt!.Value);
    }

    // ── seed descriptor ────────────────────────────────────────────────────

    [Fact]
    public async Task DescribeSeed_ReturnsFingerprintForStagedAlias()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000010"));
        var alias = MakeAlias(-42);
        _stagedRegistry.FindByAliasAsync(alias, Arg.Any<CancellationToken>()).Returns((StagedIdentity?)identity);
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)null);
        var seed = BuildSeed(alias.Value, identity, title: "Hello", type: "Task");
        _workItems.GetByIdAsync(alias.Value, Arg.Any<CancellationToken>()).Returns(seed);
        _seedLinks.GetLinksForItemAsync(alias.Value, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());

        var descriptor = await BuildService().DescribeSeedAsync(-42);

        descriptor.ShouldNotBeNull();
        descriptor!.Identity.ShouldBe(identity);
        descriptor.Alias.ShouldBe(alias);
        descriptor.Title.ShouldBe("Hello");
        descriptor.Type.ShouldBe("Task");
        descriptor.Fingerprint.Length.ShouldBe(64); // lowercase-hex SHA-256
    }

    [Fact]
    public async Task DescribeSeed_ReturnsNullForPositiveId()
    {
        (await BuildService().DescribeSeedAsync(42)).ShouldBeNull();
    }

    [Fact]
    public async Task DescribeSeed_ReturnsNullWhenAliasHasBeenPublished()
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000011"));
        var alias = MakeAlias(-42);
        _stagedRegistry.FindByAliasAsync(alias, Arg.Any<CancellationToken>()).Returns((StagedIdentity?)identity);
        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns((int?)1234);

        (await BuildService().DescribeSeedAsync(-42)).ShouldBeNull();
    }

    // ── plan complete / verified ───────────────────────────────────────────

    [Fact]
    public async Task Apply_AllVerified_TopLevelStateVerified_AndErrorNull()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 1, expectedRev: 1, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 1, Arg.Any<CancellationToken>()).Returns(2);
        _ado.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(1, rev: 2, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        apply.Error.ShouldBeNull();
        (await _journal.GetAsync(digest))!.State.ShouldBe(PlanOperationState.Verified);
    }

    // ── apply: Applying-lease ownership policy ─────────────────────────────

    [Fact]
    public async Task Apply_LostConfirmToApplyingCas_FreshLease_ReturnsTopLevelBusy_WithoutReadbackOrTermination()
    {
        // Pre-advance the row to Applying with a fresh StartedAt — the on-disk row looks
        // like an active winner holding a live lease. The service's Confirmed→Applying CAS
        // returns false; the reload observes Applying + fresh lease; the service MUST NOT
        // read back and MUST NOT terminalise (either would race a live winner's writes and
        // could poison a valid apply). It returns a top-level busy refusal.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        // Fresh (now) Applying — inside the 5-minute lease.
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, DateTimeOffset.UtcNow);

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeTrue();
        apply.Error.ShouldNotBeNull();
        apply.Error!.ShouldContain("being applied");

        // No readback and no termination. Row is still Applying with no result / applied_at
        // / verified_at / error stamped by our attempt.
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        var untouched = (await _journal.GetAsync(digest))!.Operations[0];
        untouched.State.ShouldBe(PlanOperationState.Applying);
        untouched.AppliedAt.ShouldBeNull();
        untouched.VerifiedAt.ShouldBeNull();
        untouched.Error.ShouldBeNull();
        untouched.ResultJson.ShouldBeNull();
    }

    [Fact]
    public async Task Apply_ConcurrentApplies_LoserRefusesBusy_WinnerRunsUnpoisoned()
    {
        // Two workers run ApplyAsync simultaneously. The winner has moved the row into
        // Applying (fresh StartedAt) and is mid-executor when the loser arrives. The loser
        // observes fresh Applying, returns a top-level busy refusal, and touches nothing.
        // Meanwhile the winner completes verification. A subsequent apply by the loser sees
        // the row Verified and reports success — the loser's refusal proved atomic
        // non-interference with the winner.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var patchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                patchStarted.TrySetResult(true);
                return releaseWinner.Task;
            });
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var winner = svc.ApplyAsync(file, digest);
        await patchStarted.Task; // winner has persisted Applying and is holding it

        // Loser runs against the same journal: observes fresh Applying held by winner.
        var loser = await svc.ApplyAsync(file, digest);
        loser.Failed.ShouldBeTrue();
        loser.Error!.ShouldContain("being applied");

        // Loser's refusal must NOT have poisoned the winner's row. The row is still Applying
        // with no error / applied_at planted by the loser.
        var midRun = (await _journal.GetAsync(digest))!.Operations[0];
        midRun.State.ShouldBe(PlanOperationState.Applying);
        midRun.Error.ShouldBeNull();
        midRun.AppliedAt.ShouldBeNull();

        // Release the winner and let it complete.
        releaseWinner.SetResult(4);
        var winnerResult = await winner;
        winnerResult.Failed.ShouldBeFalse();
        winnerResult.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        winnerResult.Operations[0].AppliedAt.ShouldNotBeNull(); // atomic record stamped it
        winnerResult.Operations[0].VerifiedAt.ShouldNotBeNull();

        // Winner produced a single PatchAsync call — the loser did not double-issue.
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_ExecuteReturnsApplied_UsesAtomicRecord_NoCrashWindowBetweenAppliedAndResult()
    {
        // Regression against the split TryTransition(Applying→Applied) +
        // SaveOperationResult crash window: after the executor's happy Applied path, the row
        // MUST reach at least Applied atomically with its result_json AND applied_at
        // populated in the same write. Verified assertion below proves both landed.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.AppliedAt.ShouldNotBeNull();
        row.VerifiedAt.ShouldNotBeNull();
        row.ResultJson.ShouldNotBeNullOrEmpty(); // the executor's result was recorded atomically
    }

    [Fact]
    public async Task Apply_ExecuteIndeterminate_ReadbackProvesEffect_TransitionsToVerified()
    {
        // The executor could not classify the wire response but a readback while still
        // Applying shows the effect landed. The service MUST promote the row through the
        // atomic Applying → Applied path (crash-window-free) and then Verified. This is the
        // "ambiguous committed response reconciles" acceptance path.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // PatchAsync throws a transient/indeterminate error the executor cannot classify as
        // Failed (it's not a strict-CAS conflict); readback then finds the target revision.
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("network drop mid-response"));
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.AppliedAt.ShouldNotBeNull(); // atomic record stamped it during reconciliation
        row.VerifiedAt.ShouldNotBeNull();
    }

    // ── canonical ResultJson on recovered Verified rows ────────────────────
    //
    // The Applying-recovery arm threads the readback's canonical ResultJson through the
    // atomic Applying → Applied write. A NULL result_json on a recovered Verified row
    // would silently break CLI/MCP status which reads the raw column. Each kind gets a
    // targeted assertion so a regression fails one test rather than a broad matrix.

    [Fact]
    public async Task Apply_RecoveryFromApplying_Batch_StampsCanonicalRevisionResultJson()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.ResultJson.ShouldBe("{\"revision\":4}");
    }

    [Fact]
    public async Task Apply_RecoveryFromApplying_AddLink_StampsCanonicalRevisionResultJson()
    {
        var file = WritePlan(AddLinkPlan(sourceId: 1, otherId: 9, rev: 2, relation: "successor"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        _ado.FetchWithLinksAsync(1, Arg.Any<CancellationToken>()).Returns(Task.FromResult<
            (WorkItem Item, IReadOnlyList<WorkItemLink> Links)>((
                BuildWorkItem(1, rev: 3),
                new[] { new WorkItemLink(1, 9, "System.LinkTypes.Dependency-Forward") })));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.ResultJson.ShouldBe("{\"revision\":3}");
        // Recovery MUST NOT re-issue the write.
        await _revisionBound.DidNotReceive().AddLinkAtRevisionAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_RecoveryFromApplying_Delete_StampsDeletedTrueResultJson()
    {
        var file = WritePlan(DeletePlan(workItemId: 5, rev: 6));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        _ado.FetchAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoNotFoundException(5));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.ResultJson.ShouldBe("{\"deleted\":true}");
        // Recovery MUST NOT re-issue the delete.
        await _revisionBound.DidNotReceive().DeleteAtRevisionAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_RecoveryFromApplying_MappedSeedCrash_StampsCanonicalPublishedIdResultJson()
    {
        // The mapped-seed crash: a prior run recorded the publish id map, then crashed
        // between the atomic Applying→Applied+result write and the Applied→Verified CAS.
        // Recovery reads back, finds the map row, verifies remote, and MUST stamp the
        // canonical {"identity":<planned>,"publishedId":<map>} onto the row before the
        // Applied→Verified transition. A NULL result_json here would misreport a
        // recovered publish as a resultless Verified row through CLI/MCP status.
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000401"));
        var file = WritePlan(PublishSeedPlan(identity, expectedFingerprint: "irrelevant"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        _publishIdMap.GetNewIdAsync(identity, Arg.Any<CancellationToken>()).Returns(4242);
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new PublishIntent
            {
                Identity = identity, Title = "T", TypeName = "Task",
                RecordedAt = DateTimeOffset.UtcNow, PublishedId = 4242,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        var remote = BuildWorkItem(4242, rev: 1);
        _ado.FetchWithLinksAsync(4242, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _seedLinks.GetLinksForItemAsync(4242, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SeedLink>)Array.Empty<SeedLink>());

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.AppliedAt.ShouldNotBeNull();
        row.VerifiedAt.ShouldNotBeNull();
        row.ResultJson.ShouldBe($"{{\"identity\":\"{identity}\",\"publishedId\":4242}}");
        // Recovery MUST NOT reissue a publish — the orchestrator was never invoked.
        _seedPublish.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_ExecuteIndeterminate_ReadbackProves_StampsCanonicalRevisionResultJson()
    {
        // Complement of the recovery-arm case: on the winning-execute path the executor
        // returned Indeterminate, so applyResult.ResultJson is null. The readback that
        // rescued the outcome carries the canonical shape; without threading it into
        // TryRecordAppliedAsync, the row would land Verified with a NULL result_json.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("network drop mid-response"));
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest);

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.ResultJson.ShouldBe("{\"revision\":4}");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private string WritePlan(string source, string name = "plan.json")
    {
        var file = Path.Combine(_repoRoot, name);
        File.WriteAllText(file, source);
        return file;
    }

    private static string ValidPlanSource(string org = "acme", string project = "cache") => $$"""
        {
          "version": 1,
          "workspace": { "organization": "{{org}}", "project": "{{project}}" },
          "operations": [
            { "id": "op-1", "kind": "batch", "workItemId": 1, "expectedRevision": 1,
              "fields": { "System.State": "Active" } }
          ]
        }
        """;

    private static string BatchOnlyPlan(int workItemId, int expectedRev, string state) => $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "cache" },
          "operations": [
            { "id": "op", "kind": "batch", "workItemId": {{workItemId}}, "expectedRevision": {{expectedRev}},
              "fields": { "System.State": "{{state}}" } }
          ]
        }
        """;

    private static string TwoBatchPlan((int id, int rev) a, (int id, int rev) b) => $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "cache" },
          "operations": [
            { "id": "op-a", "kind": "batch", "workItemId": {{a.id}}, "expectedRevision": {{a.rev}},
              "fields": { "System.State": "Active" } },
            { "id": "op-b", "kind": "batch", "workItemId": {{b.id}}, "expectedRevision": {{b.rev}},
              "fields": { "System.State": "Closed" } }
          ]
        }
        """;

    private static string AddLinkPlan(int sourceId, int otherId, int rev, string relation) => $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "cache" },
          "operations": [
            { "id": "L", "kind": "add-link", "workItemId": {{sourceId}}, "expectedRevision": {{rev}},
              "relation": "{{relation}}", "otherId": {{otherId}} }
          ]
        }
        """;

    private static string DeletePlan(int workItemId, int rev) => $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "cache" },
          "operations": [
            { "id": "D", "kind": "delete", "workItemId": {{workItemId}}, "expectedRevision": {{rev}} }
          ]
        }
        """;

    private static string PublishSeedPlan(StagedIdentity identity, string expectedFingerprint) => $$"""
        {
          "version": 1,
          "workspace": { "organization": "acme", "project": "cache" },
          "operations": [
            { "id": "S", "kind": "publish-seed",
              "stagedIdentity": "{{identity}}",
              "expectedFingerprint": "{{expectedFingerprint}}" }
          ]
        }
        """;

    private static WorkItem BuildWorkItem(int id, int rev, string? state = null)
    {
        var wi = new WorkItem { Id = id, Title = $"item-{id}" };
        wi.MarkSynced(rev);
        if (state is not null)
        {
            wi.ChangeState(state);
            wi.UpdateField("System.State", state);
        }
        return wi;
    }

    private static WorkItem BuildSeed(int id, StagedIdentity identity, string title, string type)
    {
        return new WorkItem
        {
            Id = id,
            Title = title,
            Type = WorkItemType.Parse(type).Value,
            IsSeed = true,
            StagedIdentity = identity,
        };
    }

    private static StagedAlias MakeAlias(int negative)
    {
        StagedAlias.TryFrom(negative, out var alias).ShouldBeTrue();
        return alias;
    }

    // ── fakes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake SeedPublishOrchestrator wrapper: real orchestrators need eight repositories the
    /// executor never even reaches on the code paths under test (map hit + fingerprint
    /// drift), so we construct a real orchestrator over NSubstitute stubs and intercept the
    /// call count.
    /// </summary>
    private sealed class FakeSeedPublish
    {
        public int CallCount { get; private set; }
        public SeedPublishOrchestrator Orchestrator { get; }

        public FakeSeedPublish()
        {
            // Real orchestrator over dummy dependencies. Under the covered scenarios its
            // PublishAsync is never invoked; a call would be a bug the test suite catches.
            Orchestrator = new SeedPublishOrchestrator(
                Substitute.For<IWorkItemRepository>(),
                Substitute.For<IAdoWorkItemService>(),
                Substitute.For<ISeedLinkRepository>(),
                Substitute.For<IWorkItemLinkRepository>(),
                Substitute.For<IPublishIdMapRepository>(),
                Substitute.For<ISeedPublishRulesProvider>(),
                Substitute.For<IUnitOfWork>(),
                new BacklogOrderer(Substitute.For<IAdoWorkItemService>(),
                    Substitute.For<IFieldDefinitionStore>()),
                Substitute.For<IPendingChangeStore>(),
                Substitute.For<IPublishIntentRepository>());
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
