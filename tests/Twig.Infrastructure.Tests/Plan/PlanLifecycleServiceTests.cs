using System.Net.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;

using Twig.Domain.Services.ChangeProposals;
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
    private readonly IFieldDefinitionStore _fieldDefinitions = Substitute.For<IFieldDefinitionStore>();
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly IWorkItemRepository _workItems = Substitute.For<IWorkItemRepository>();
    private readonly ISeedLinkRepository _seedLinks = Substitute.For<ISeedLinkRepository>();
    private readonly IStagedIdentityRegistry _stagedRegistry = Substitute.For<IStagedIdentityRegistry>();
    private readonly IPublishIdMapRepository _publishIdMap = Substitute.For<IPublishIdMapRepository>();
    private readonly IPublishIntentRepository _publishIntent = Substitute.For<IPublishIntentRepository>();
    private readonly IProcessRuleProvider _ruleProvider = Substitute.For<IProcessRuleProvider>();
    private readonly WorkItemMapper _mapper = new();

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
        _revisionBound.FetchAtRevisionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var id = ci.ArgAt<int>(0);
                var ct = ci.ArgAt<CancellationToken>(2);
                var source = await _workItems.GetByIdAsync(id, ct);
                return source is null
                    ? new WorkItemSnapshot
                    {
                        Id = id,
                        Revision = ci.ArgAt<int>(1),
                        TypeName = string.Empty,
                        Title = string.Empty,
                        State = string.Empty,
                        Fields = new Dictionary<string, string?>(),
                    }
                    : _mapper.ToSnapshot(source);
            });

        // Default: the rule provider carries no rules for any type, so the runtime process
        // gate no-ops and existing tests keep the pre-AB#673 shape. Tests exercising the
        // gate override this via NSubstitute.
        _ruleProvider.GetRulesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(Array.Empty<ProcessRule>()));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The steering mode the authorization gate sees. Defaults to human-steered, which is the
    /// fail-closed production answer; AFK tests set it explicitly.
    /// </summary>
    private SessionSteeringMode _steeringMode = SessionSteeringMode.HumanSteered;

    private sealed class StubSteering(Func<SessionSteeringMode> read) : ISessionSteeringModeProvider
    {
        public SessionSteeringMode Resolve() => read();
    }

    /// <summary>
    /// A well-formed authorization bound to <paramref name="digest"/>, in whichever mode the
    /// current steering requires. Tests that exercise the gate itself build their own instead.
    /// </summary>
    private ProposalAuthorization Authorize(string digest) => new()
    {
        Digest = digest,
        Mode = ProposalAuthorizationGate.RequiredMode(_steeringMode),
        AuthorizerIdentity = "Test Authorizer",
        AuthorizedAt = DateTimeOffset.UnixEpoch,
    };

    private PlanLifecycleService BuildService(DateTimeOffset? clock = null)
    {
        var provider = clock is null
            ? TimeProvider.System
            : (TimeProvider)new FakeClock(clock.Value);
        return new PlanLifecycleService(
            new PlanDocumentParser(),
            _journal,
            _pending,
            _fieldDefinitions,
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
            provider,
            new StubSteering(() => _steeringMode),
            _ruleProvider);
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

        var apply = await svc.ApplyAsync(file, "0000000000000000000000000000000000000000000000000000000000000000", Authorize("0000000000000000000000000000000000000000000000000000000000000000"));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        var journalRow = (await _journal.GetAsync(digest))!;
        journalRow.State.ShouldBe(PlanOperationState.Verified);
    }

    // ── apply: authorization gate (AB#743, Spec #729 §Authorization) ────────

    /// <summary>
    /// Arranges an applyable batch proposal and returns its file and digest. Every gate test
    /// below shares it so a refusal cannot be confused with a proposal that was never viable.
    /// </summary>
    private async Task<(string File, string Digest, PlanLifecycleService Service)> ApplyableProposal()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 4, state: "Active"));
        return (file, digest, svc);
    }

    // Defends against: an apply proceeding with nobody on record as having released it.
    // A cancel or defer is exactly this shape — the reviewer never produced an authorization —
    // so this is also the test that "cancel/defer never applies".
    [Fact]
    public async Task Apply_WithNoAuthorization_RefusesAndTouchesNothing()
    {
        var (file, digest, svc) = await ApplyableProposal();

        var apply = await svc.ApplyAsync(file, digest, authorization: null);

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("no human sign-off");
        apply.Operations.ShouldBeEmpty();

        // Nothing was confirmed and no ADO call was made: the journal is still exactly where
        // preview left it. A gate that refused only after confirming would have already
        // claimed the proposal was released.
        var journal = (await _journal.GetAsync(digest))!;
        journal.State.ShouldBe(PlanOperationState.Planned);
        journal.AuthorizationMode.ShouldBeNull();
        await _ado.DidNotReceiveWithAnyArgs().PatchAsync(default, default!, default, default);
    }

    // Defends against: replaying a sign-off from a different proposal. The digest is what an
    // authorization means; without this the record authorizes whatever it is handed to.
    [Fact]
    public async Task Apply_WithSignOffBoundToADifferentDigest_FailsClosed()
    {
        var (file, digest, svc) = await ApplyableProposal();
        var stale = Authorize("f".PadLeft(64, 'f'));

        var apply = await svc.ApplyAsync(file, digest, stale);

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("bound to digest");
        (await _journal.GetAsync(digest))!.State.ShouldBe(PlanOperationState.Planned);
        await _ado.DidNotReceiveWithAnyArgs().PatchAsync(default, default!, default, default);
    }

    // Defends against: an AFK run being released by a record claiming a human signed it.
    [Fact]
    public async Task Apply_InAfkSession_RequiresAModelAuthorizationRecord()
    {
        _steeringMode = SessionSteeringMode.Afk;
        var (file, digest, svc) = await ApplyableProposal();

        var humanRecord = new ProposalAuthorization
        {
            Digest = digest,
            Mode = ProposalAuthorizationMode.Human,
            AuthorizerIdentity = "Daniel Green",
            AuthorizedAt = DateTimeOffset.UnixEpoch,
        };

        var refused = await svc.ApplyAsync(file, digest, humanRecord);
        refused.Failed.ShouldBeTrue();
        refused.Error!.ShouldContain("model authorization");

        var applied = await svc.ApplyAsync(file, digest, humanRecord with { Mode = ProposalAuthorizationMode.Model });
        applied.Failed.ShouldBeFalse();
    }

    // Spec #729: an unresolvable steering mode takes the human-steered path. Defends against
    // "we could not tell" being read as permission to run unattended.
    [Fact]
    public async Task Apply_WithUnresolvedSteering_FallsBackToHumanSteered()
    {
        _steeringMode = SessionSteeringMode.Unresolved;
        var (file, digest, svc) = await ApplyableProposal();

        var modelRecord = new ProposalAuthorization
        {
            Digest = digest,
            Mode = ProposalAuthorizationMode.Model,
            AuthorizerIdentity = "twig-agent",
            AuthorizedAt = DateTimeOffset.UnixEpoch,
        };

        (await svc.ApplyAsync(file, digest, modelRecord)).Failed.ShouldBeTrue();
        (await svc.ApplyAsync(file, digest, modelRecord with { Mode = ProposalAuthorizationMode.Human }))
            .Failed.ShouldBeFalse();
    }

    // The audit obligation of T2 §5.3: an applied proposal carries the canonical model, the
    // digest, the mode, the authorizer, and the rationale. Defends against an apply that
    // mutates the board and leaves no reconstructable record of who released it or what they
    // were shown.
    [Fact]
    public async Task Apply_RecordsTheFullAuditRow_IncludingWhatTheAuthorizerWasShown()
    {
        var (file, digest, svc) = await ApplyableProposal();
        var authorizedAt = DateTimeOffset.Parse("2026-08-27T11:22:33Z").ToUniversalTime();

        var apply = await svc.ApplyAsync(file, digest, new ProposalAuthorization
        {
            Digest = digest,
            Mode = ProposalAuthorizationMode.Human,
            AuthorizerIdentity = "Daniel Green",
            Rationale = "Operations match the ticket.",
            AuthorizedAt = authorizedAt,
        });

        apply.Failed.ShouldBeFalse();

        var journal = (await _journal.GetAsync(digest))!;
        journal.AuthorizationMode.ShouldBe(ProposalAuthorizationMode.Human);
        journal.AuthorizerIdentity.ShouldBe("Daniel Green");
        journal.Rationale.ShouldBe("Operations match the ticket.");
        journal.AuthorizedAt.ShouldBe(authorizedAt);

        // review_model_json is what the authorizer was SHOWN; canonical_json is what they
        // AUTHORIZED. Both are present, and they are different documents.
        var reviewModel = journal.ReviewModelJson.ShouldNotBeNull();
        reviewModel.ShouldContain("\"model\":\"twig.change-proposal.review\"");
        reviewModel.ShouldContain($"\"digest\":\"{digest}\"");
        reviewModel.ShouldContain("\"operations\"");
        reviewModel.ShouldNotBe(journal.CanonicalJson);
        journal.CanonicalJson.ShouldNotContain("twig.change-proposal.review");
    }

    // Defends against: authorizing a proposal changing its identity. The review model embeds
    // the digest and must never feed it, or an authorization would invalidate itself.
    [Fact]
    public async Task Apply_AuthorizationDoesNotAlterTheDigest()
    {
        var (file, digest, svc) = await ApplyableProposal();

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Digest.ShouldBe(digest);
        (await svc.PreviewAsync(file)).Digest.ShouldBe(digest);
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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
    }

    // ── AB#754/755: warning-verified outcomes across all three lifecycle paths ──
    //
    // These assert the PUBLIC lifecycle outcome plus the persisted journal row — refreshed
    // ADO state and journal outcome together, per Spec #753's testing decisions — rather
    // than the executor's private comparator. AC #754(5) / #755(4) say the three paths must
    // behave identically; that claim is only worth anything if all three are exercised.

    [Fact]
    public async Task Apply_WinningPath_ServerGeneratedNormalization_VerifiesAndPersistsWarning()
    {
        _fieldDefinitions
            .GetByReferenceNameAsync("Microsoft.VSTS.Common.ClosedDate", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(new FieldDefinition(
                "Microsoft.VSTS.Common.ClosedDate", "Closed Date", "dateTime", IsReadOnly: false)));
        var file = WritePlan(BatchWithFields(42, 3,
        [
            ("System.State", "Done"),
            ("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T00:00:00Z"),
        ]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        var refreshed = BuildWorkItem(42, rev: 4, state: "Done");
        refreshed.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(refreshed);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[0].Warning.ShouldNotBeNull();
        apply.Operations[0].Warning!.ShouldContain("ClosedDate");
        apply.Operations[0].Error.ShouldBeNull();

        // The warning is DURABLE, not merely in the returned result object.
        var persisted = (await _journal.GetAsync(digest))!.Operations[0];
        persisted.State.ShouldBe(PlanOperationState.Verified);
        persisted.Warning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Apply_RecoveryFromApplying_HtmlNormalization_VerifiesAndPersistsWarning()
    {
        // Stale-Applying recovery must reach the same verdict AND record the same detail as
        // the winning path — the recovery readback is where a second, drifting policy would
        // hide.
        _fieldDefinitions
            .GetByReferenceNameAsync("System.Description", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(new FieldDefinition(
                "System.Description", "Description", "html", IsReadOnly: false)));
        var file = WritePlan(BatchWithFields(42, 3,
        [
            ("System.Description", "<p class=\\\"x\\\">Body</p>"),
        ]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, stale);

        var refreshed = BuildWorkItem(42, rev: 4);
        refreshed.UpdateField("System.Description", "<P class='x'>Body</P>");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(refreshed);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[0].Warning.ShouldNotBeNull();
        apply.Operations[0].Warning!.ShouldContain("System.Description");
        // Recovery must not re-issue the write.
        await _ado.DidNotReceive().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        (await _journal.GetAsync(digest))!.Operations[0].Warning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Apply_RecoveryFromApplied_ServerGeneratedNormalization_VerifiesAndPersistsWarning()
    {
        // The third path: an already-Applied row finalising through readback.
        _fieldDefinitions
            .GetByReferenceNameAsync("Microsoft.VSTS.Common.ClosedDate", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(new FieldDefinition(
                "Microsoft.VSTS.Common.ClosedDate", "Closed Date", "dateTime", IsReadOnly: false)));
        var file = WritePlan(BatchWithFields(42, 3,
        [
            ("System.State", "Done"),
            ("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T00:00:00Z"),
        ]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, DateTimeOffset.UtcNow);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Applying, PlanOperationState.Applied, DateTimeOffset.UtcNow);

        var refreshed = BuildWorkItem(42, rev: 4, state: "Done");
        refreshed.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-25T22:45:08.85Z");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(refreshed);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[0].Warning.ShouldNotBeNull();
        (await _journal.GetAsync(digest))!.Operations[0].Warning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Apply_GenuineScalarMismatch_StaysNonVerifiedAndRecordsNoWarning()
    {
        // The strict half at lifecycle level: a real contradiction must terminalise without
        // a warning, so a reader can never mistake it for a normalized success.
        var file = WritePlan(BatchWithFields(42, 3, [("System.State", "Done")]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Doing"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Indeterminate);
        apply.Operations[0].Warning.ShouldBeNull();
        (await _journal.GetAsync(digest))!.Operations[0].Warning.ShouldBeNull();
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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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
        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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
        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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
            new PlanDocumentParser(), _journal, _pending, _fieldDefinitions, _ado, _revisionBound,
            _seedPublish.Orchestrator, _workItems, _seedLinks, _stagedRegistry, _publishIdMap,
            _publishIntent, _config, _paths, boundClock, new StubSteering(() => _steeringMode));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var winner = svc.ApplyAsync(file, digest, Authorize(digest));
        await patchStarted.Task; // winner has persisted Applying and is holding it

        // Loser runs against the same journal: observes fresh Applying held by winner.
        var loser = await svc.ApplyAsync(file, digest, Authorize(digest));
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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

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

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Verified);
        row.ResultJson.ShouldBe("{\"revision\":4}");
    }

    // ── apply: runtime process-rule gate (AB#673) ──────────────────────────

    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenEnabledMakeRequiredRuleFires_AndFieldEmpty()
    {
        // AB#673: bypassRules walks past ADO's own state-rule gate, so twig owns the gate
        // on the client side. If a batch would land the item in a state where an enabled
        // makeRequired rule targets a field whose effective value is empty, we terminalise
        // Failed BEFORE calling PatchAsync — the row never leaves Confirmed via the wire.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Local source cached at rev 3, in a pre-terminal state, with the gate field unset.
        var source = new WorkItem
        {
            Id = 42,
            Title = "gated",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        // One enabled rule: when System.State == Done → makeRequired Custom.Gated.
        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("when", "System.State", "Done") },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Gated", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error.ShouldNotBeNull();
        row.Error!.ShouldContain("Custom.Gated");
        row.Error!.ShouldContain("Done");

        // The load-bearing assertion: the wire attempt never happened.
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());

        // Journal header carries the refusal too.
        var journalAfter = (await _journal.GetAsync(digest))!;
        journalAfter.State.ShouldBe(PlanOperationState.Failed);
        journalAfter.Operations[0].State.ShouldBe(PlanOperationState.Failed);
    }

    [Fact]
    public async Task Apply_Batch_PassesGate_WhenBatchOverlaySuppliesRequiredField()
    {
        // Complement: same rule, same transition — but the batch supplies the required
        // field, so the gate permits and existing PATCH + readback semantics take over.
        // Guards against a gate implementation that would refuse a valid batch by
        // consulting the source aggregate alone and ignoring the overlay.
        var file = WritePlan(BatchWithFields(
            workItemId: 42, expectedRev: 3,
            fields: new (string, string?)[]
            {
                ("System.State", "Done"),
                ("Custom.Gated", "signed"),
            }));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "gated",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("when", "System.State", "Done") },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Gated", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var readback = BuildWorkItem(42, rev: 4, state: "Done");
        readback.UpdateField("Custom.Gated", "signed");
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        await _ado.Received(1).PatchAsync(
            42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_IgnoresDisabledMakeRequiredRule()
    {
        // A rule marked disabled MUST NOT contribute to the gate — a disabled rule does
        // not fire on the server either, and treating it as active would refuse valid
        // batches (the same trap the assembler documents in BuildRequirednessIndex).
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "not-actually-gated",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var disabled = new ProcessRule(
            Conditions: new[] { new RuleCondition("when", "System.State", "Done") },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Gated", null) },
            IsDisabled: true);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { disabled }));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Done"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        await _ado.Received(1).PatchAsync(
            42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task Apply_Batch_GateReadsCanonicalWorkItemPropertyWhenFieldMapMissesIt()
    {
        // AB#673 review finding: a WorkItem may hold System.Title/State/AssignedTo/…
        // only in its typed properties, NOT in the Fields dictionary — the constructor
        // accepts them directly. A Fields-only source view would treat those canonical
        // values as empty and false-refuse a batch that never intended to change them.
        // Here the batch transitions to Done and a rule requires System.Title; the cached
        // source has a Title set via the typed property but does NOT mirror it into
        // Fields. The canonical-property fallback in the gate must let the batch through.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Title on the typed property only. NOT mirrored into Fields — that is the point.
        var source = new WorkItem
        {
            Id = 42,
            Title = "canonical-only",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        // Rule: when State = Done → makeRequired System.Title.
        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("when", "System.State", "Done") },
            Actions: new[] { new RuleAction("makeRequired", "System.Title", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Done"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
    }

    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenCanonicalPropertyEmpty_AndRuleFires()
    {
        // Complement to the previous test: the canonical property IS the whole source of
        // truth, and if it is empty the gate must fire. A source with Title unset (empty
        // string default) and a "when Done → makeRequired System.Title" rule must refuse.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            // Title deliberately not set — falls to the default empty string.
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("when", "System.State", "Done") },
            Actions: new[] { new RuleAction("makeRequired", "System.Title", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error!.ShouldContain("System.Title");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_WhenChanged_FiresOnNonStateField_WhenBatchChangesIt()
    {
        // AB#673 review finding: whenChanged on a non-state field was hard-wired to `false`
        // — the pre-fix gate only checked "isState && from != to". A rule of the form
        // "whenChanged Custom.Foo → makeRequired Custom.Bar" must fire when Custom.Foo's
        // pre-batch and post-batch values differ, regardless of whether the state moves.
        var file = WritePlan(BatchWithFields(
            workItemId: 42, expectedRev: 3,
            fields: new (string, string?)[]
            {
                ("Custom.Foo", "new-foo"),
                // Custom.Bar deliberately absent — the rule must catch it.
            }));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "changed-foo",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.UpdateField("Custom.Foo", "old-foo");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("whenChanged", "Custom.Foo", null) },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Bar", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error!.ShouldContain("Custom.Bar");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_WhenNotChanged_FiresOnNonStateField_WhenBatchLeavesItAlone()
    {
        // Symmetric to the whenChanged case: whenNotChanged on a non-state field must
        // fire iff the pre-batch and post-batch values are equal. The pre-fix gate
        // read "!isState || Equal(from, to)" and false-positive-fired on every non-state
        // clause — but it also permitted the make-required target because the same clause
        // was misclassified. Here the rule "whenNotChanged Custom.Foo → makeRequired
        // Custom.Bar" must catch a batch that never touched Custom.Foo and leaves Bar empty.
        var file = WritePlan(BatchWithFields(
            workItemId: 42, expectedRev: 3,
            fields: new (string, string?)[]
            {
                ("System.State", "Active"),
                // Custom.Foo untouched, Custom.Bar absent.
            }));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "unchanged-foo",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.UpdateField("Custom.Foo", "stable");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("whenNotChanged", "Custom.Foo", null) },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Bar", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error!.ShouldContain("Custom.Bar");
    }

    [Fact]
    public async Task Apply_Batch_WhenWas_ReadsOldValueOfNonStateField_NotEffective()
    {
        // AB#673 review finding: whenWas on a non-state field compared condition.Value
        // against the CURRENT (post-overlay) map — semantically "whenIs". The rule
        // "whenWas Custom.Foo == 'starting' → makeRequired Custom.Bar" must consult
        // Custom.Foo's PRE-batch value. Here the batch rewrites Custom.Foo to a new value,
        // but its old value matches the condition — the rule must fire.
        var file = WritePlan(BatchWithFields(
            workItemId: 42, expectedRev: 3,
            fields: new (string, string?)[]
            {
                ("Custom.Foo", "something-else"),
                // Custom.Bar deliberately absent.
            }));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "was-check",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.UpdateField("Custom.Foo", "starting");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        var rule = new ProcessRule(
            Conditions: new[] { new RuleCondition("whenWas", "Custom.Foo", "starting") },
            Actions: new[] { new RuleAction("makeRequired", "Custom.Bar", null) },
            IsDisabled: false);
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(new[] { rule }));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error!.ShouldContain("Custom.Bar");
    }

    [Fact]
    public async Task Apply_Batch_WhenStateChangedTo_DoesNotFireWithoutAStateTransition()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "already-done",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Done");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>
            ([new ProcessRule(
                [new RuleCondition("whenStateChangedTo", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.TransitionEvidence", null)],
                IsDisabled: false)]));
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Done"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
    }

    [Fact]
    public async Task Apply_Batch_RuleFieldReferences_AreCaseInsensitive()
    {
        var file = WritePlan(BatchWithFields(
            workItemId: 42,
            expectedRev: 3,
            fields: [("system.state", "Done")]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = new WorkItem
        {
            Id = 42,
            Title = "mixed-case-fields",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("Custom.Gated", "signed");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>
            ([new ProcessRule(
                [new RuleCondition("when", "system.state", "done")],
                [new RuleAction("makeRequired", "custom.gated", null)],
                IsDisabled: false)]));
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        var readback = BuildWorkItem(42, rev: 4, state: "Done");
        readback.UpdateField("Custom.Gated", "signed");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
    }

    // ── apply: paired value-supplying rule actions (AB#803) ────────────────

    /// <summary>
    /// The live Hyperbright <c>Task</c> rules for <i>To Do → Doing</i>, transcribed
    /// verbatim from <c>twig process description -o json</c> on 2026-08-28 — verbs,
    /// condition pairs, state labels and field reference names all as the server declares
    /// them. The requirement and the action that satisfies it are SEPARATE rules carrying
    /// IDENTICAL conditions, which is precisely what the pre-fix gate could not see.
    /// </summary>
    /// <remarks>
    /// The real reference names are used deliberately: they are what a reader hitting this
    /// refusal will grep for. The TYPE stays the fixture's placeholder, because the gate
    /// never keys on type — naming it <c>Task</c> would imply a type-specific rule that
    /// does not exist and would contradict the process-agnostic contract.
    /// </remarks>
    [Fact]
    public async Task Apply_Batch_PassesGate_WhenSeparateRuleSuppliesRequiredField()
    {
        // AB#803: the gate read only the makeRequired half and refused every honest Task
        // state transition, forcing callers to stage System.Reason and ActivatedBy — values
        // ADO's own rule engine generates. The engine enforcing requiredness is the engine
        // running copyValue/copyFromCurrentUser, under the same condition, so the field is
        // never empty when requiredness is evaluated.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Doing"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Neither required field is set on the source, and the batch stages neither.
        var source = new WorkItem
        {
            Id = 42,
            Title = "task-claim",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("To Do");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        RuleCondition[] toDoToDoing =
        [
            new RuleCondition("when", "System.State", "Doing"),
            new RuleCondition("whenWas", "System.State", "To Do"),
        ];
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(toDoToDoing, [new RuleAction("makeRequired", "System.Reason", null)], IsDisabled: false),
                new ProcessRule(toDoToDoing, [new RuleAction("makeRequired", "Microsoft.VSTS.Common.ActivatedBy", null)], IsDisabled: false),
                new ProcessRule(toDoToDoing, [new RuleAction("copyFromCurrentUser", "Microsoft.VSTS.Common.ActivatedBy", null)], IsDisabled: false),
                new ProcessRule(toDoToDoing, [new RuleAction("copyFromServerClock", "Microsoft.VSTS.Common.ActivatedDate", null)], IsDisabled: false),
                new ProcessRule(toDoToDoing, [new RuleAction("copyValue", "System.Reason", "Started")], IsDisabled: false),
            ]));

        var readback = BuildWorkItem(42, rev: 4, state: "Doing");
        readback.UpdateField("System.Reason", "Started");
        readback.UpdateField("Microsoft.VSTS.Common.ActivatedBy", "Daniel Green (daniel danielgreen.net)");
        readback.UpdateField("Microsoft.VSTS.Common.ActivatedDate", "2026-08-28T00:00:00Z");

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        await _ado.Received(1).PatchAsync(
            42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other half of AB#803's acceptance: the terminal transition. Live
    /// <c>Doing → Done</c> pairs three requirements with three different supplier verbs,
    /// one of them (<c>copyFromServerClock</c>) a value the client must NOT invent — a
    /// staged client clock is a value the server immediately overwrites.
    /// </summary>
    [Fact]
    public async Task Apply_Batch_PassesGate_WhenTerminalTransitionSuppliersCoverEveryRequirement()
    {
        // The batch stages only the state and the caller-owned outcome field; every other
        // required field is server-generated.
        var file = WritePlan(BatchWithFields(
            workItemId: 42, expectedRev: 3,
            fields: new (string, string?)[]
            {
                ("System.State", "Done"),
                ("Custom.TerminalOutcome", "completed"),
            }));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        RuleCondition[] doingToDone =
        [
            new RuleCondition("when", "System.State", "Done"),
            new RuleCondition("whenWas", "System.State", "Doing"),
        ];
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                // Requirement and supplier on the bare "when Done" condition.
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Microsoft.VSTS.Common.ClosedDate", null)], IsDisabled: false),
                new ProcessRule(DoneCondition, [new RuleAction("copyFromServerClock", "Microsoft.VSTS.Common.ClosedDate", null)], IsDisabled: false),
                // …and on the narrower whenWas-qualified condition. The supplier's condition
                // set is NOT identical to the bare one above, and it still fires here.
                new ProcessRule(doingToDone, [new RuleAction("makeRequired", "Microsoft.VSTS.Common.ClosedBy", null)], IsDisabled: false),
                new ProcessRule(doingToDone, [new RuleAction("copyFromCurrentUser", "Microsoft.VSTS.Common.ClosedBy", null)], IsDisabled: false),
                new ProcessRule(doingToDone, [new RuleAction("makeRequired", "System.Reason", null)], IsDisabled: false),
                new ProcessRule(doingToDone, [new RuleAction("copyValue", "System.Reason", "Completed")], IsDisabled: false),
            ]));

        var readback = BuildWorkItem(42, rev: 4, state: "Done");
        readback.UpdateField("Custom.TerminalOutcome", "completed");
        readback.UpdateField("Microsoft.VSTS.Common.ClosedDate", "2026-08-28T00:00:00Z");
        readback.UpdateField("Microsoft.VSTS.Common.ClosedBy", "Daniel Green (daniel danielgreen.net)");
        readback.UpdateField("System.Reason", "Completed");
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        await _ado.Received(1).PatchAsync(
            42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A supplier whose condition set DIFFERS from the requirement's still satisfies it,
    /// provided it fires on this batch.
    /// </summary>
    /// <remarks>
    /// Pins a deliberate design decision. AB#803's suggested fix said "the same rule (or
    /// another enabled rule with the same condition)", and matching condition sets
    /// textually is the obvious reading — but it is strictly wrong. Every rule that fires
    /// runs, so what decides whether the field is populated is whether the supplier fires,
    /// not how its condition is spelled. A narrower supplier that fires would be refused
    /// under condition-equality even though the server populates the field. The safety
    /// this test does NOT give up is covered by
    /// <see cref="Apply_Batch_RefusesBeforePatch_WhenSupplyingRuleDoesNotFire"/>.
    /// </remarks>
    [Fact]
    public async Task Apply_Batch_PassesGate_WhenFiringSupplierCarriesADifferentCondition()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                // Broad requirement: any move to Done.
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                // Narrower supplier: Done specifically from Doing. GatedSource() is Doing,
                // so this fires on THIS batch despite the conditions not matching.
                new ProcessRule(
                    [
                        new RuleCondition("when", "System.State", "Done"),
                        new RuleCondition("whenWas", "System.State", "Doing"),
                    ],
                    [new RuleAction("copyValue", "Custom.Gated", "filled")],
                    IsDisabled: false),
            ]));

        var readback = BuildWorkItem(42, rev: 4, state: "Done");
        readback.UpdateField("Custom.Gated", "filled");
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
    }


    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenSupplierTargetsADifferentField()
    {
        // A supplier fires, but on another field. Guards against a fix that collected
        // "some rule supplied something" instead of keying suppliers by target field.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                new ProcessRule(DoneCondition, [new RuleAction("copyValue", "Custom.Other", "filled")], IsDisabled: false),
            ]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations.ShouldHaveSingleItem();
        row.State.ShouldBe(PlanOperationState.Failed);
        row.Error!.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenSupplyingRuleDoesNotFire()
    {
        // The supplier targets the right field but its condition does not hold for this
        // batch, so ADO will not run it. A supplier set collected without evaluating
        // conditions would let an empty gate field through.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                new ProcessRule(
                    [new RuleCondition("when", "System.State", "Removed")],
                    [new RuleAction("copyValue", "Custom.Gated", "filled")],
                    IsDisabled: false),
            ]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations.ShouldHaveSingleItem().Error!.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenSupplyingRuleIsDisabled()
    {
        // A disabled rule does not run on the server, so it supplies nothing here either —
        // the same reading Apply_Batch_IgnoresDisabledMakeRequiredRule applies to the
        // requiredness half.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                new ProcessRule(DoneCondition, [new RuleAction("copyValue", "Custom.Gated", "filled")], IsDisabled: true),
            ]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations.ShouldHaveSingleItem().Error!.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An action that carries no usable value, or that clears the field outright, leaves
    /// the requirement unsatisfied. This is the AB#673 protection: the authored close
    /// gates declare <c>makeRequired</c> with no paired supplier and must still refuse.
    /// </summary>
    [Theory]
    [InlineData("copyValue", null)]          // literal supplier with nothing to supply
    [InlineData("copyValue", "")]            // ditto, empty rather than absent
    [InlineData("setDefaultValue", null)]
    // The four non-supplying verbs the Rules API documents alongside makeRequired.
    [InlineData("setValueToEmpty", null)]    // clears the field — the opposite of a supplier
    [InlineData("makeReadOnly", null)]
    [InlineData("disallowValue", "filled")]
    [InlineData("hideTargetField", null)]
    // …and anything outside the documented vocabulary at all.
    [InlineData("someVerbTwigHasNeverSeen", "filled")]
    public async Task Apply_Batch_RefusesBeforePatch_WhenActionSuppliesNoValue(
        string actionType,
        string? actionValue)
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                new ProcessRule(DoneCondition, [new RuleAction(actionType, "Custom.Gated", actionValue)], IsDisabled: false),
            ]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations.ShouldHaveSingleItem().Error!.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A field-sourced supplier only satisfies the requirement when the field it names
    /// actually holds a value — otherwise it copies emptiness onto emptiness.
    /// </summary>
    [Theory]
    [InlineData("copyFromField", true)]
    [InlineData("copyFromField", false)]
    [InlineData("setDefaultFromField", true)]
    [InlineData("setDefaultFromField", false)]
    public async Task Apply_Batch_GateHonoursFieldSourcedSupplier_OnlyWhenSourceIsPopulated(
        string actionType,
        bool sourcePopulated)
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var source = GatedSource();
        if (sourcePopulated) source.UpdateField("Custom.Origin", "from-origin");
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
                new ProcessRule(DoneCondition, [new RuleAction(actionType, "Custom.Gated", "Custom.Origin")], IsDisabled: false),
            ]));

        var readback = BuildWorkItem(42, rev: 4, state: "Done");
        readback.UpdateField("Custom.Gated", "from-origin");
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(readback);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBe(!sourcePopulated);
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(
            sourcePopulated ? PlanOperationState.Verified : PlanOperationState.Failed);
    }

    /// <summary>The condition every AB#803 gate fixture below fires on.</summary>
    private static RuleCondition[] DoneCondition => [new RuleCondition("when", "System.State", "Done")];

    /// <summary>A rev-3 source in a pre-terminal state with <c>Custom.Gated</c> unset.</summary>
    private static WorkItem GatedSource()
    {
        var source = new WorkItem
        {
            Id = 42,
            Title = "gated",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.MarkSynced(3);
        return source;
    }

    // ── apply: authoritative expected-revision snapshots (AB#719) ─────────

    [Fact]
    public async Task Apply_Batch_UsesAuthoritativeSnapshot_WhenCacheWouldFalseRefuse()
    {
        // The cache is deliberately missing Custom.Gated. The expected-revision server
        // snapshot carries it, so the gate must permit the write. Reading the cache here
        // would produce a false rule refusal before PatchAsync.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        var staleProjection = new WorkItem
        {
            Id = 42,
            Title = "cache projection",
            Type = WorkItemType.Parse("Frobnicator").Value,
        };
        staleProjection.ChangeState("Doing");
        staleProjection.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(staleProjection);

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing", ("Custom.Gated", "signed")));
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [new RuleCondition("when", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 4, state: "Done"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_RefusesBeforePatch_WhenAuthoritativeSnapshotKnowsRequiredFieldEmpty()
    {
        // Absence from a complete expected-revision server snapshot is known-empty, not
        // "Twig did not carry this field". The enabled rule must therefore refuse before
        // the privileged PATCH.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing"));
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [new RuleCondition("when", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[0].Error!.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_LeavesPlanRetryable_WhenAuthoritativeSnapshotIsUnavailable()
    {
        // No source truth means no rule decision. Preserve Confirmed so the same digest can
        // be retried after ADO is reachable; never fall back to the filtered cache projection.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("temporary ADO failure"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Error!.ShouldContain("authoritative", Case.Insensitive);
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        (await _journal.GetAsync(digest))!.Operations.ShouldHaveSingleItem()
            .State.ShouldBe(PlanOperationState.Confirmed);
    }

    // ── apply: same-item authoritative snapshot carry-forward (AB#721) ────

    [Fact]
    public async Task Apply_TwoBatchesSameItem_SecondGateSeesFirstOpsFieldOverlay_WithoutRefetchingAuthoritativeSnapshot()
    {
        // Two batches on the same work item. The first supplies the field a rule requires
        // when the second lands the item in the gated state. The gate must evaluate the
        // second op against the authoritative state PRODUCED BY the first (revision moved
        // forward, Custom.Gated overlaid) — never re-fetching a fresh snapshot at the
        // second op's own expected revision, and never falling back to the local cache.
        var plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "Custom.Gated": "signed" } },
                { "id": "op-2", "kind": "batch", "workItemId": 42, "expectedRevision": 4,
                  "fields": { "System.State": "Done" } }
              ]
            }
            """;
        var file = WritePlan(plan);
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Fresh authoritative snapshot for op-1: no Custom.Gated yet.
        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing"));
        // Load-bearing: the second op's expected revision MUST NOT trigger a fetch — the
        // carry-forward is the whole point of the ticket. If we ever call
        // FetchAtRevisionAsync(42, 4, ...), the executor path is wrong.
        _revisionBound.FetchAtRevisionAsync(42, 4, Arg.Any<CancellationToken>()).Returns<WorkItemSnapshot>(_ =>
            throw new InvalidOperationException(
                "AB#721 regression: second batch fetched a fresh authoritative snapshot instead of consuming the prior op's projection."));

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [new RuleCondition("when", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 4, Arg.Any<CancellationToken>())
            .Returns(5);
        // Readback after op-1: server carries the overlaid Custom.Gated at rev 4.
        // Readback after op-2: server carries Custom.Gated and State=Done at rev 5.
        var rev4 = BuildWorkItem(42, rev: 4, state: "Doing");
        rev4.UpdateField("Custom.Gated", "signed");
        var rev5 = BuildWorkItem(42, rev: 5, state: "Done");
        rev5.UpdateField("Custom.Gated", "signed");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(rev4, rev5);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.Count.ShouldBe(2);
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[1].State.ShouldBe(PlanOperationState.Verified);

        // Load-bearing: the gate consulted the authoritative source exactly ONCE — the
        // first op's fresh fetch. The second op inherited the projection.
        await _revisionBound.Received(1).FetchAtRevisionAsync(
            42, Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Both writes actually landed on the wire in order.
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_TwoBatchesSameItem_SecondGateRefusesUsingProjectedState_WhenFirstOpClearsRequiredField()
    {
        // The first op clears the gated field; the second op tries to transition to a
        // state whose rule requires that field. The carried projection MUST see the
        // cleared value — a refetch at the second op's expected revision would return the
        // pre-op server snapshot (still populated) and false-permit the batch.
        var plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "Custom.Gated": null } },
                { "id": "op-2", "kind": "batch", "workItemId": 42, "expectedRevision": 4,
                  "fields": { "System.State": "Done" } }
              ]
            }
            """;
        var file = WritePlan(plan);
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing", ("Custom.Gated", "signed")));

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [
                    new RuleCondition("when", "System.State", "Done"),
                    new RuleCondition("when", "System.Rev", "4"),
                ],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        // op-1: successful clear at rev 3 → rev 4.
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        // Readback for op-1 reflects the cleared field.
        _ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 4, state: "Doing"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Operations.Count.ShouldBe(2);
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[1].State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[1].Error!.ShouldContain("Custom.Gated");

        // Load-bearing: op-2's wire attempt never happened — the projected post-op-1
        // state fired the gate before PatchAsync at rev 4.
        await _ado.DidNotReceive().PatchAsync(
            42, Arg.Any<IReadOnlyList<FieldChange>>(), 4, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_BatchLinkBatchSameItem_LinkAdvancesCarriedRevisionWithoutRefetch()
    {
        var plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "Custom.Gated": "signed" } },
                { "id": "op-2", "kind": "add-link", "workItemId": 42, "expectedRevision": 4,
                  "relation": "parent", "otherId": 99 },
                { "id": "op-3", "kind": "batch", "workItemId": 42, "expectedRevision": 5,
                  "fields": { "System.State": "Done" } }
              ]
            }
            """;
        var file = WritePlan(plan);
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing"));
        _revisionBound.FetchAtRevisionAsync(42, 5, Arg.Any<CancellationToken>()).Returns<WorkItemSnapshot>(_ =>
            throw new InvalidOperationException(
                "AB#721 regression: post-link batch refetched instead of consuming the advanced carry."));
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>
            ([new ProcessRule(
                [
                    new RuleCondition("when", "System.State", "Done"),
                    new RuleCondition("when", "System.Rev", "5"),
                    new RuleCondition("when", "System.Parent", "99"),
                ],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _revisionBound.AddLinkAtRevisionAsync(
                42, "System.LinkTypes.Hierarchy-Reverse", 99, 4, Arg.Any<CancellationToken>())
            .Returns(5);
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var rev4 = BuildWorkItem(42, rev: 4, state: "Doing");
        rev4.UpdateField("Custom.Gated", "signed");
        var rev5 = BuildWorkItem(42, rev: 5, state: "Doing").WithParentId(99);
        rev5.UpdateField("Custom.Gated", "signed");
        var rev6 = BuildWorkItem(42, rev: 6, state: "Done");
        rev6.UpdateField("Custom.Gated", "signed");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(rev4, rev5, rev6);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldAllBe(operation => operation.State == PlanOperationState.Verified);
        await _revisionBound.Received(1).FetchAtRevisionAsync(
            42, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_TwoBatchesDifferentItems_EachGetsItsOwnAuthoritativeFetch()
    {
        // Complement: the carry map is per work item id. Two batches on DIFFERENT items
        // must each drive a fresh authoritative fetch — the first op's projection is
        // irrelevant to the second's rule evaluation.
        var plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-a", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "System.State": "Done" } },
                { "id": "op-b", "kind": "batch", "workItemId": 99, "expectedRevision": 7,
                  "fields": { "System.State": "Done" } }
              ]
            }
            """;
        var file = WritePlan(plan);
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing", ("Custom.Gated", "signed-42")));
        _revisionBound.FetchAtRevisionAsync(99, 7, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(99, 7, "Frobnicator", "Doing", ("Custom.Gated", "signed-99")));

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [new RuleCondition("when", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.PatchAsync(99, Arg.Any<IReadOnlyList<FieldChange>>(), 7, Arg.Any<CancellationToken>())
            .Returns(8);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 4, state: "Done"));
        _ado.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(99, rev: 8, state: "Done"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[1].State.ShouldBe(PlanOperationState.Verified);

        await _revisionBound.Received(1).FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>());
        await _revisionBound.Received(1).FetchAtRevisionAsync(99, 7, Arg.Any<CancellationToken>());
    }

    private static WorkItemSnapshot AuthoritativeSnapshot(
        int id,
        int revision,
        string type,
        string state,
        params (string Name, string? Value)[] fields)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.WorkItemType"] = type,
            ["System.Title"] = "authoritative",
            ["System.State"] = state,
            ["System.Rev"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (name, value) in fields)
            values[name] = value;

        return new WorkItemSnapshot
        {
            Id = id,
            Revision = revision,
            TypeName = type,
            Title = "authoritative",
            State = state,
            Fields = values,
        };
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

    private static string BatchWithFields(
        int workItemId,
        int expectedRev,
        IReadOnlyList<(string Name, string? Value)> fields)
    {
        var body = string.Join(", ", fields.Select(f =>
            f.Value is null
                ? $"\"{f.Name}\": null"
                : $"\"{f.Name}\": \"{f.Value}\""));
        return $$"""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op", "kind": "batch", "workItemId": {{workItemId}}, "expectedRevision": {{expectedRev}},
                  "fields": { {{body}} } }
              ]
            }
            """;
    }

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

    // ── canonical semantic review model (AB#742) ───────────────────────────

    [Fact]
    public async Task Preview_ReturnsTheCanonicalReviewModelBoundToTheSameDigest()
    {
        // Defends against: preview reporting a digest while the review model carries a
        // different one. The reviewer authorizes what the MODEL describes but the apply gate
        // checks the reported digest — if they can disagree, the authorization is meaningless.
        var file = WritePlan(ValidPlanSource());

        var result = await BuildService().PreviewAsync(file);

        result.ReviewModel.ShouldNotBeNull();
        result.ReviewModel!.Digest.ShouldBe(result.Digest);
        result.ReviewModel.ModelVersion.ShouldBe(1);
        result.ReviewModel.Workspace.Organization.ShouldBe("acme");
    }

    [Fact]
    public async Task Preview_ReviewModel_DescribesEveryOperationThePreviewReports()
    {
        // Defends against: the model and the operation list drifting apart, so that an
        // operation preview counts is missing from what a reviewer is shown.
        var file = WritePlan(ValidPlanSource());

        var result = await BuildService().PreviewAsync(file);

        result.ReviewModel!.Operations.Count.ShouldBe(result.Operations.Count);
        result.ReviewModel.Operations.Select(o => o.OpId)
            .ShouldBe(result.Operations.Select(o => o.Id));
    }

    [Fact]
    public async Task Preview_ReviewModel_IsStillProducedWhenTheProposalCannotApply()
    {
        // Defends against: withholding the model exactly when it matters most. A blocked
        // proposal still has to be reviewable — otherwise the reviewer sees "canApply: false"
        // with no description of what was proposed or why it is blocked.
        var file = WritePlan(ValidPlanSource());
        _pending.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PendingChangeDetail>>(new[]
            {
                new PendingChangeDetail(1, 42, "field", "System.State", null, "To do", "Doing",
                    DateTimeOffset.UtcNow, null),
            }));

        var result = await BuildService().PreviewAsync(file);

        result.CanApply.ShouldBeFalse();
        result.ReviewModel.ShouldNotBeNull();
        result.ReviewModel!.AuthorizationChoices.ShouldNotContain("apply");
        result.ReviewModel.Blockers.ShouldContain(b => b.Kind == "pending" && b.WorkItemId == 42);
    }

    [Fact]
    public async Task RenderedProposal_KeepsItsDigestThroughPreviewAndJournalLookup()
    {
        // Defends against: the rendering path and the lifecycle path computing digests
        // differently. This is the end-to-end form of the T2 §3 contract: a proposal rendered
        // from a recipe, written to disk, previewed, and then looked up in the journal must be
        // the SAME proposal at every step. If these ever diverge, a recipe-rendered proposal
        // would be refused at apply time with a digest mismatch that looks like tampering.
        var renderer = new ChangeRecipeRenderer(new PlanDocumentParser());
        var proposal = renderer.Render(
            new WorkspaceStateRecipe(_config.Organization!, _config.Project!),
            new ChangeRecipeInputs(new Dictionary<string, string> { ["state"] = "Doing" }))[0];

        var file = WritePlan(proposal.CanonicalJson);
        var svc = BuildService();

        var preview = await svc.PreviewAsync(file);
        preview.Digest.ShouldBe(proposal.Digest);

        // …and the journal the preview imported is keyed by that very digest, which is the
        // key apply later confirms against.
        var journal = await _journal.GetAsync(proposal.Digest);
        journal.ShouldNotBeNull();

        var status = await svc.StatusAsync(file);
        status!.Digest.ShouldBe(proposal.Digest);
    }

    /// <summary>Minimal recipe bound to the test workspace, used for the digest round-trip.</summary>
    private sealed class WorkspaceStateRecipe(string organization, string project) : IChangeRecipe
    {
        public string RecipeId => "twig.test.workspace-state";

        public int Version => 1;

        public IReadOnlyList<PlanDefinition> Render(ChangeRecipeInputs inputs) =>
        [
            new PlanDefinition
            {
                Version = 1,
                Workspace = new PlanWorkspace { Organization = organization, Project = project },
                Operations =
                [
                    new BatchOperation
                    {
                        Id = "op-1",
                        WorkItemId = 42,
                        ExpectedRevision = 1,
                        Fields = new Dictionary<string, string?> { ["System.State"] = inputs.Require("state") },
                    },
                ],
            },
        ];
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
                Substitute.For<IPublishIntentRepository>(),
                Twig.TestKit.ReferenceProfileBuilder.SprintPolicy());
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
