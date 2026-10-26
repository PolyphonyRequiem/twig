using System.Globalization;
using System.Text.Json;
using System.Net.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Tests.Ado;
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
    // Feature #1178: the service is handed the provider-neutral mutation port, so every journal,
    // partial-failure and readback pin here proves proposal apply needs nothing ADO-only. The port is
    // the real ADO adapter over this client substitute (AC (a)), so the opaque revision precondition
    // check runs exactly as in production. Wrapped once in the constructor, before any per-test stub.
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IProviderWorkItemWriter _writer;
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
        _writer = AdoWriterFixture.Wrap(_ado);
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
                        TypeName = "FixtureType",
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
    private int _steeringResolutions;

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

    private sealed class FixtureOrigin(string root) : IPlanOriginProvider
    {
        public Task<PlanOrigin> GetOriginAsync(CancellationToken ct = default) => Task.FromResult(
            new PlanOrigin(root, "fixture-fingerprint", 1, "fixture-connection", "fixture-binding", 1,
                "fixture-selection", 1, "fixture-identity", "aad", "fixture-credential", "fixture-tenant",
                "fixture-object", "fixture-issuer", "fixture-authority", null));
    }

    private PlanLifecycleService BuildService(DateTimeOffset? clock = null, IPlanJournalRepository? journal = null,
        IProviderWorkItemWriter? writer = null, IPlanOriginProvider? origin = null,
        IWorkItemSnapshotReader? providerSnapshots = null,
        IWorkItemRepository? workItems = null,
        IWorkItemDiscussionReader? discussionReader = null,
        IWorkItemDiscussionWriter? discussionWriter = null,
        DiscussionAppendAdmission? discussionAdmission = null,
        TimeProvider? timeProvider = null,
        string? systemRegistryPath = null,
        string? startDir = null)
    {
        var provider = timeProvider ?? (clock is null
            ? TimeProvider.System
            : new FakeClock(clock.Value));
        var paths = startDir is null ? _paths : new TwigPaths(
            _paths.TwigDir, _paths.ConfigPath, _paths.DbPath, startDir);
        return new PlanLifecycleService(
            new PlanDocumentParser(),
            journal ?? _journal,
            _pending,
            _fieldDefinitions,
            writer ?? _writer,
            AdoWriterFixture.Codec,
            _revisionBound,
            _seedPublish.Orchestrator,
            workItems ?? _workItems,
            _seedLinks,
            _stagedRegistry,
            _publishIdMap,
            _publishIntent,
            _config,
            paths,
            provider,
            new StubSteering(() => { _steeringResolutions++; return _steeringMode; }),
            origin ?? new FixtureOrigin(_repoRoot),
            _ruleProvider,
            Substitute.For<IUnitOfWork>(),
            providerSnapshots: providerSnapshots,
            discussionReader: discussionReader,
            discussionWriter: discussionWriter,
            discussionAdmission: discussionAdmission,
            systemRegistryPath: systemRegistryPath);
    }

    [Fact]
    public async Task Status_CaseInsensitiveComparer_MatchesHistoricalAliasRows()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"));

        using var journalStore = new SqliteCacheStore("Data Source=:memory:");
        var journal = new SqlitePlanJournalRepository(journalStore)
        {
            SourcePathComparer = StringComparer.OrdinalIgnoreCase,
        };
        var svc = BuildService(journal: journal);

        var originalDigest = (await svc.PreviewAsync(file)).Digest!;
        var sourcePath = await GetJournaledSourcePathAsync(originalDigest, journal);

        var aliasSource = BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed");
        var alias = new PlanDocumentParser().Parse(aliasSource);
        var aliasPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath).ToUpperInvariant());
        await journal.ImportAsync(alias.Plan!, alias.CanonicalJson!, alias.Digest!, aliasPath, DateTimeOffset.UtcNow.AddMinutes(5));

        var status = await svc.StatusAsync(file);

        status.ShouldNotBeNull();
        status!.Found.ShouldBeTrue();
        status.Digest.ShouldBe(originalDigest);
        var replacement = status.Replacement.ShouldNotBeNull();
        replacement.SourcePath.ShouldBe(sourcePath);
        replacement.CurrentDigest.ShouldBe(originalDigest);
        replacement.CurrentDigestJournaled.ShouldBeTrue();
        replacement.SupersededDigests.ShouldBe([alias.Digest!]);

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

    [Fact]
    public async Task Preview_V2ShowsIdentityAndBlockerWithoutJournalImport()
    {
        var file = WritePlan(ProviderPlanSource());
        var journal = Substitute.For<IPlanJournalRepository>();
        var result = await BuildService(journal: journal).PreviewAsync(file);

        result.Digest.ShouldNotBeNullOrWhiteSpace();
        result.CanApply.ShouldBeFalse();
        result.Issues.ShouldContain(issue => issue.Code == PlanValidationCodes.UnsupportedApplyVersion);
        result.ReviewModel.ShouldNotBeNull();
        result.ReviewModel!.AuthorizationChoices.ShouldBe(["revise", "decline"]);
        result.ReviewModel.Blockers.ShouldContain(blocker =>
            blocker.Kind == "issue" && blocker.Detail.Contains(PlanValidationCodes.UnsupportedApplyVersion));
        var reviewedTarget = result.ReviewModel.Operations.Single().Target.ProviderWorkItem;
        reviewedTarget.ShouldNotBeNull();
        reviewedTarget!.ExternalIdentity.ProviderKind.ShouldBe("github");
        reviewedTarget.LocalId.IsEmpty.ShouldBeFalse();
        journal.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Preview_V2ShowsProviderPolicyDecisionAndKeepsApplyBlocked()
    {
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_policy");
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.FetchSnapshotAsync(identity, Arg.Any<CancellationToken>())
            .Returns(new ProviderWorkItemSnapshot(
                identity,
                RevisionToken: null,
                FieldValues.Empty
                    .Set("System.WorkItemType", new TextValue("Task"))
                    .Set("System.Tags", new StringListValue(["bug"])),
                Relationships: [],
                RelationshipsComplete: true));
        await File.WriteAllTextAsync(_paths.PolicyPath, """
            { "version": 1, "rules": [
              { "id": "priority", "kind": "label-applicability",
                "prefix": "priority:", "allowedTypes": ["Issue"] }
            ] }
            """);
        var source = WritePlan(ProviderBatchPlanSource(identity));

        var result = await BuildService(providerSnapshots: reader).PreviewAsync(source);

        result.CanApply.ShouldBeFalse();
        result.Issues.ShouldContain(issue => issue.Code == PlanValidationCodes.UnsupportedApplyVersion);
        var decision = result.ReviewModel!.PolicyDecisions.ShouldHaveSingleItem();
        decision.Outcome.ShouldBe(Twig.Domain.Services.Policy.PolicyOutcome.Refuse);
        decision.Reason.ShouldContain("I_policy");
        decision.Reason.ShouldContain("not exact-revision apply authority");
        result.ReviewModel.Blockers.ShouldContain(blocker => blocker.Kind == "policy");
        await reader.Received(1).FetchSnapshotAsync(identity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_V1ParentRetypeWithoutCompleteChildrenShowsBlockingPolicyDecision()
    {
        var parent = new WorkItem
        {
            Id = 2,
            Title = "parent",
            Type = Twig.Domain.ValueObjects.WorkItemType.Parse("Issue").Value,
        };
        _workItems.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<WorkItem>)call.Arg<IEnumerable<int>>()
                .Where(id => id == parent.Id).Select(_ => parent).ToList());
        await File.WriteAllTextAsync(_paths.PolicyPath, """
            { "version": 1, "rules": [
              { "id": "task-parent", "kind": "parent-type",
                "childType": "Task", "allowedParentTypes": ["Issue"] }
            ] }
            """);
        var source = WritePlan("""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "parent-retype", "kind": "batch", "workItemId": 2, "expectedRevision": 1,
                  "fields": { "System.WorkItemType": "Epic" } }
              ]
            }
            """);

        var result = await BuildService().PreviewAsync(source);

        result.CanApply.ShouldBeFalse();
        var decision = result.ReviewModel.ShouldNotBeNull().PolicyDecisions.ShouldHaveSingleItem();
        decision.Outcome.ShouldBe(Twig.Domain.Services.Policy.PolicyOutcome.Unavailable);
        decision.OpId.ShouldBe("parent-retype");
        decision.WorkItemId.ShouldBe(2);
        result.ReviewModel.Blockers.ShouldContain(blocker => blocker.Kind == "policy");
    }

    [Fact]
    public async Task Preview_V1PreservesCanonicalRequestJsonInTheJournal()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Doing"));

        var preview = await BuildService().PreviewAsync(file);

        preview.CanApply.ShouldBeTrue();
        var journal = (await _journal.GetAsync(preview.Digest!))!;
        journal.Operations.ShouldHaveSingleItem().RequestJson.ShouldBe(
            "{\"expectedRevision\":3,\"fields\":{\"System.State\":\"Doing\"},\"id\":\"op\",\"kind\":\"batch\",\"workItemId\":42}");
    }

    [Fact]
    public async Task Preview_ExpectedDigestMismatchRefusesBeforeJournalImportOrPendingSnapshot()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        var actualDigest = (await svc.ValidateAsync(file)).Digest!;
        var expectedDigest = actualDigest[..^1] + (actualDigest[^1] == '0' ? '1' : '0');

        var result = await svc.PreviewExpectedAsync(file, expectedDigest);

        result.Issues.ShouldContain(issue => issue.Code == PlanValidationCodes.DigestMismatch);
        result.ReviewModel.ShouldBeNull();
        (await _journal.GetAsync(actualDigest)).ShouldBeNull();
        await _pending.DidNotReceive().GetAllChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Latest_RefusesChangedOrMissingSelectedFileWithoutFallingBack()
    {
        var firstFile = WritePlan(ValidPlanSource(), "older.json");
        var latestFile = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"), "latest.json");
        var firstTime = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var latestTime = firstTime.AddMinutes(1);
        var first = await BuildService(firstTime).PreviewAsync(firstFile);
        var latest = await BuildService(latestTime).PreviewAsync(latestFile);

        var selected = await BuildService().LatestAsync();
        selected.Found.ShouldBeTrue();
        Path.IsPathFullyQualified(selected.File!).ShouldBeTrue();
        Path.GetFileName(selected.File).ShouldBe("latest.json");
        (await File.ReadAllTextAsync(selected.File!)).ShouldBe(await File.ReadAllTextAsync(latestFile));
        selected.Digest.ShouldBe(latest.Digest);

        await File.WriteAllTextAsync(latestFile, BatchOnlyPlan(workItemId: 816, expectedRev: 2, state: "Closed"));
        var changed = await BuildService().LatestAsync();
        changed.Error!.ShouldContain("changed after preview");
        changed.Digest.ShouldBe(latest.Digest);
        changed.Digest.ShouldNotBe(first.Digest);

        File.Delete(latestFile);
        var missing = await BuildService().LatestAsync();
        missing.Error!.ShouldContain("Failed to read plan file");
        missing.Digest.ShouldBe(latest.Digest);
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
        var error = apply.Error ?? throw new InvalidOperationException("Expected the apply to report an error.");
        error.ShouldContain("No preview journal");
    }

    [Fact]
    public async Task Apply_V2RefusesBeforeConfirmationAuthorizationJournalPendingOrProviderAccess()
    {
        var file = WritePlan(ProviderPlanSource());
        var journal = Substitute.For<IPlanJournalRepository>();
        var service = BuildService(journal: journal);

        var result = await service.ApplyAsync(file, "unconfirmed-digest", authorization: null);

        result.Failed.ShouldBeTrue();
        var error = result.Error ?? throw new InvalidOperationException("Expected the apply to report an error.");
        error.ShouldContain("cannot be applied");
        error.ShouldContain("No confirmation");
        journal.ReceivedCalls().ShouldBeEmpty();
        await _pending.DidNotReceive().GetAllChangesAsync(Arg.Any<CancellationToken>());
        _ado.ReceivedCalls().ShouldBeEmpty();
        _revisionBound.ReceivedCalls().ShouldBeEmpty();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_DraftCapturedJournal_RefusesBeforeAuthorityOrConfirmation(bool supplyAuthorization)
    {
        var repository = new SqliteWorkItemRepository(_store, _mapper);
        await repository.SaveAsync(_mapper.Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 3, Title = "baseline", TypeName = "FixtureType", State = "FixtureState",
            Fields = new Dictionary<string, string?> { ["Fixture.Field"] = "old" },
        }));
        var snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(
            new QualifiedWorkItemKey(_repoRoot, "fixture-connection", "fixture-binding", 42), 3)
            .SetField("Fixture.Field", "old", "intent");
        (await new SqliteRecoverableWorkItemDraftStore(_store).TrySaveAsync(draft, null)).ShouldBeTrue();
        var journal = await new SqliteDraftProposalCaptureStore(_store).CaptureAsync(
            draft, snapshot, await new FixtureOrigin(_repoRoot).GetOriginAsync(),
            new PlanWorkspace { Organization = "acme", Project = "cache" }, _repoRoot);
        await File.WriteAllTextAsync(journal.SourcePath, journal.CanonicalJson);
        journal.DraftCapture.ShouldNotBeNull();

        var result = await BuildService().ApplyAsync(journal.SourcePath, journal.Digest,
            supplyAuthorization ? Authorize(journal.Digest) : null);

        result.Failed.ShouldBeTrue();
        result.Error!.ShouldContain("Draft-captured proposals are local inspection only");
        _steeringResolutions.ShouldBe(0);
        var retained = (await _journal.GetAsync(journal.Digest))!;
        retained.State.ShouldBe(PlanOperationState.Planned);
        retained.ConfirmedAt.ShouldBeNull();
        retained.AuthorizationMode.ShouldBeNull();
        _ado.ReceivedCalls().ShouldBeEmpty();
        _revisionBound.ReceivedCalls().ShouldBeEmpty();
        _ruleProvider.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_DraftCapturedJournal_WithUnavailableOrigin_RetainsIntentWithoutAuthority()
    {
        var repository = new SqliteWorkItemRepository(_store, _mapper);
        await repository.SaveAsync(_mapper.Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 3, Title = "baseline", TypeName = "FixtureType", State = "FixtureState",
            Fields = new Dictionary<string, string?> { ["Fixture.Field"] = "old" },
        }));
        var snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(
            new QualifiedWorkItemKey(_repoRoot, "fixture-connection", "fixture-binding", 42), 3)
            .SetField("Fixture.Field", "old", "intent");
        var drafts = new SqliteRecoverableWorkItemDraftStore(_store);
        (await drafts.TrySaveAsync(draft, null)).ShouldBeTrue();
        var journal = await new SqliteDraftProposalCaptureStore(_store).CaptureAsync(
            draft, snapshot, await new FixtureOrigin(_repoRoot).GetOriginAsync(),
            new PlanWorkspace { Organization = "acme", Project = "cache" }, _repoRoot);
        await File.WriteAllTextAsync(journal.SourcePath, journal.CanonicalJson);
        using var retainedRow = _store.GetConnection().CreateCommand();
        retainedRow.CommandText = """
            SELECT json_array(digest,canonical_json,origin_json,draft_capture_json,state,confirmed_at,
                authorization_mode,last_previewed_at,error) FROM pending.proposal_journals WHERE digest=@digest;
            """;
        retainedRow.Parameters.AddWithValue("@digest", journal.Digest);
        var before = (string)retainedRow.ExecuteScalar()!;
        var origin = Substitute.For<IPlanOriginProvider>();
        origin.GetOriginAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Current principal is unavailable."));

        var result = (await BuildService(origin: origin).StatusAsync(journal.SourcePath)).ShouldNotBeNull();

        result.Found.ShouldBeTrue();
        result.Digest.ShouldBe(journal.Digest);
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("draft-capture-stale");
        issue.Path.ShouldBe(string.Empty);
        issue.Message.ShouldBe("Native capture origin is unavailable or changed: Current principal is unavailable.");
        await origin.Received(1).GetOriginAsync(Arg.Any<CancellationToken>());
        ((string)retainedRow.ExecuteScalar()!).ShouldBe(before);
        (await File.ReadAllTextAsync(journal.SourcePath)).ShouldBe(journal.CanonicalJson);
        JsonSerializer.Serialize(await drafts.GetAsync(draft.ItemKey),
            Twig.Infrastructure.Serialization.TwigJsonContext.Default.RecoverableWorkItemDraft)
            .ShouldBe(JsonSerializer.Serialize(draft,
                Twig.Infrastructure.Serialization.TwigJsonContext.Default.RecoverableWorkItemDraft));
        journal.State.ShouldBe(PlanOperationState.Planned);
        journal.ConfirmedAt.ShouldBeNull();
        journal.AuthorizationMode.ShouldBeNull();
        _steeringResolutions.ShouldBe(0);
        _ado.ReceivedCalls().ShouldBeEmpty();
        _revisionBound.ReceivedCalls().ShouldBeEmpty();
        _ruleProvider.ReceivedCalls().ShouldBeEmpty();
    }

    // Defends against: a draft captured under a checkout root reached through a directory
    // alias (macOS /var -> /private/var, a Windows junction) being reported as moved by
    // status, which resolves every path component before comparing with the retained path.
    [Fact]
    public async Task Status_DraftCapturedThroughAliasedCheckoutRoot_ReachesOriginCheck()
    {
        var alias = Path.Combine(Path.GetTempPath(), $"twig-plan-alias-{Guid.NewGuid():N}");
        CreateDirectoryAlias(alias, _repoRoot);
        try
        {
            var journal = await CaptureDraftAsync(alias, Path.Combine(alias, ".twig", "proposals"));
            journal.SourcePath.ShouldNotStartWith(alias, Case.Insensitive, "new captures retain the canonical path");
            var legacy = Path.GetFullPath(Path.Combine(alias, ".twig", "proposals", Path.GetFileName(journal.SourcePath)));
            RetainLegacySpelling(journal.Digest, legacy, journal.SourcePath);
            var origin = UnavailableOrigin();

            var result = (await BuildService(origin: origin).StatusAsync(legacy)).ShouldNotBeNull();

            result.Found.ShouldBeTrue();
            result.Issues.ShouldHaveSingleItem().Message
                .ShouldBe("Native capture origin is unavailable or changed: Current principal is unavailable.");
            await origin.Received(1).GetOriginAsync(Arg.Any<CancellationToken>());
            RetainedSourcePath(journal.Digest).ShouldBe(legacy, "status must not rewrite the retained row");
        }
        finally
        {
            RemoveDirectoryAlias(alias);
        }
    }

    // Defends against: path resolution turning a copy at a different inside path into a match.
    [Fact]
    public async Task Status_DraftCopiedToDifferentInsidePath_RefusesBeforeOriginCheck()
    {
        var journal = await CaptureDraftAsync(_repoRoot, Path.Combine(_repoRoot, ".twig", "proposals"));
        var copy = Path.Combine(_repoRoot, ".twig", "elsewhere", Path.GetFileName(journal.SourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(journal.SourcePath, copy);
        var origin = UnavailableOrigin();

        var result = (await BuildService(origin: origin).StatusAsync(copy)).ShouldNotBeNull();

        result.Issues.ShouldHaveSingleItem().Message.ShouldBe("The file is not at the retained native capture source path.");
        origin.ReceivedCalls().ShouldBeEmpty();
    }

    // Defends against: a retained path recorded through an alias that leads outside the
    // workspace being accepted because the inside copy has the same digest.
    [Fact]
    public async Task Status_RetainedPathAliasEscapingWorkspace_RefusesBeforeOriginCheck()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"twig-plan-outside-{Guid.NewGuid():N}");
        var alias = Path.Combine(Path.GetTempPath(), $"twig-plan-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        CreateDirectoryAlias(alias, outside);
        try
        {
            var journal = await CaptureDraftAsync(_repoRoot, alias);
            var inside = Path.Combine(_repoRoot, ".twig", "proposals", Path.GetFileName(journal.SourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            File.Copy(journal.SourcePath, inside);
            var origin = UnavailableOrigin();

            var result = (await BuildService(origin: origin).StatusAsync(inside)).ShouldNotBeNull();

            var message = result.Issues.ShouldHaveSingleItem().Message;
            message.ShouldStartWith("The retained native capture source path is not usable in this workspace: ");
            message.ShouldContain("is outside the workspace root");
            origin.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            RemoveDirectoryAlias(alias);
            Directory.Delete(outside, recursive: true);
        }
    }

    // Defends against: a retained path whose link was retargeted still being treated as
    // the location of the file captured through it.
    [Fact]
    public async Task Status_RetainedPathLinkRetargeted_RefusesBeforeOriginCheck()
    {
        var first = Path.Combine(_repoRoot, "first");
        var second = Path.Combine(_repoRoot, "second");
        var link = Path.Combine(_repoRoot, "proposals-link");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        CreateDirectoryAlias(link, first);
        try
        {
            var journal = await CaptureDraftAsync(_repoRoot, link);
            var captured = Path.Combine(first, Path.GetFileName(journal.SourcePath));
            File.Exists(captured).ShouldBeTrue("the capture must have been written through the link");
            RetainLegacySpelling(journal.Digest, Path.GetFullPath(Path.Combine(link, Path.GetFileName(journal.SourcePath))), journal.SourcePath);
            RemoveDirectoryAlias(link);
            CreateDirectoryAlias(link, second);
            var origin = UnavailableOrigin();

            var result = (await BuildService(origin: origin).StatusAsync(captured)).ShouldNotBeNull();

            result.Issues.ShouldHaveSingleItem().Message.ShouldBe("The file is not at the retained native capture source path.");
            origin.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            RemoveDirectoryAlias(link);
        }
    }

    // Defends against: AB#832 replacement detection going blind for a draft captured through a
    // checkout alias, because status looks the resolved path up while the row retains the alias.
    // The overwrite is then reported as a plan that was never previewed.
    [Fact]
    public async Task Status_DraftCapturedThroughAliasThenOverwritten_ReportsReplacement()
    {
        var alias = Path.Combine(Path.GetTempPath(), $"twig-plan-alias-{Guid.NewGuid():N}");
        CreateDirectoryAlias(alias, _repoRoot);
        try
        {
            var journal = await CaptureDraftAsync(alias, Path.Combine(alias, ".twig", "proposals"));
            File.WriteAllText(journal.SourcePath, BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed"));

            var status = (await BuildService().StatusAsync(journal.SourcePath)).ShouldNotBeNull();

            status.Found.ShouldBeFalse();
            var replacement = status.Replacement.ShouldNotBeNull();
            replacement.SupersededDigests.ShouldBe([journal.Digest]);
            replacement.CurrentDigestJournaled.ShouldBeFalse();
            replacement.CurrentDigest.ShouldNotBe(journal.Digest);
            RetainedSourcePath(journal.Digest).ShouldBe(journal.SourcePath, "status must not rewrite the retained row");
        }
        finally
        {
            RemoveDirectoryAlias(alias);
        }
    }

    // Defends against: replacement detection going blind for a row retained by the pre-canonical
    // writer, which stored Path.GetFullPath of the alias spelling. New captures are canonical, so
    // only the legacy fallback can find this row; the fixture rewrites one test-owned row only.
    [Fact]
    public async Task Status_LegacyRawAliasRowThenOverwritten_ReportsReplacementThroughAliasAndCanonical()
    {
        var real = Path.Combine(_repoRoot, "real-proposals");
        var link = Path.Combine(_repoRoot, "proposals-link");
        Directory.CreateDirectory(real);
        CreateDirectoryAlias(link, real);
        try
        {
            var journal = await CaptureDraftAsync(_repoRoot, link);
            var canonical = journal.SourcePath;
            var legacy = Path.GetFullPath(Path.Combine(link, Path.GetFileName(canonical)));
            RetainLegacySpelling(journal.Digest, legacy, canonical);
            File.WriteAllText(canonical, BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed"));

            foreach (var spelling in new[] { legacy, canonical })
            {
                var status = (await BuildService().StatusAsync(spelling)).ShouldNotBeNull(spelling);

                status.Found.ShouldBeFalse(spelling);
                var replacement = status.Replacement.ShouldNotBeNull(spelling);
                replacement.SupersededDigests.ShouldBe([journal.Digest]);
                replacement.CurrentDigestJournaled.ShouldBeFalse();
            }
            RetainedSourcePath(journal.Digest).ShouldBe(legacy, "status must not rewrite the retained row");
        }
        finally
        {
            RemoveDirectoryAlias(link);
        }
    }

    // Defends against: alias-tolerant replacement detection attributing a capture to a different
    // physical file that a retargeted link now reaches. Different bytes are written at the new
    // target, so no journal exists for them and only a false attribution could produce a result.
    [Fact]
    public async Task Status_FileReachedThroughRetargetedCaptureLink_IsNotAttributedTheCapture()
    {
        var first = Path.Combine(_repoRoot, "first");
        var second = Path.Combine(_repoRoot, "second");
        var link = Path.Combine(_repoRoot, "proposals-link");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        CreateDirectoryAlias(link, first);
        try
        {
            var journal = await CaptureDraftAsync(_repoRoot, link);
            File.Exists(Path.Combine(first, Path.GetFileName(journal.SourcePath)))
                .ShouldBeTrue("the capture must have been written through the link");
            RemoveDirectoryAlias(link);
            CreateDirectoryAlias(link, second);
            var other = Path.Combine(second, Path.GetFileName(journal.SourcePath));
            File.WriteAllText(other, BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed"));

            var status = await BuildService().StatusAsync(other);

            status.ShouldBeNull("nothing was journaled for the file the link now reaches");
        }
        finally
        {
            RemoveDirectoryAlias(link);
        }
    }

    // Defends against: a new capture retaining the link path instead of the file it was written
    // to, so an identical copy reached after the link is retargeted inherits the capture.
    // Rows captured before canonical retention lack the physical path and cannot be re-proven.
    [Fact]
    public async Task Status_SameBytesReachedThroughRetargetedCaptureLink_RefusesBeforeOriginCheck()
    {
        var first = Path.Combine(_repoRoot, "first");
        var second = Path.Combine(_repoRoot, "second");
        var link = Path.Combine(_repoRoot, "proposals-link");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        CreateDirectoryAlias(link, first);
        try
        {
            var journal = await CaptureDraftAsync(_repoRoot, link);
            var captured = Path.Combine(first, Path.GetFileName(journal.SourcePath));
            File.Exists(captured).ShouldBeTrue("the capture must have been written through the link");
            journal.SourcePath.ShouldBe(CanonicalPathResolver.Resolve(captured).Path, "a new capture retains the physical file, not the link spelling");
            RemoveDirectoryAlias(link);
            CreateDirectoryAlias(link, second);
            var copy = Path.Combine(second, Path.GetFileName(journal.SourcePath));
            File.Copy(captured, copy);
            var origin = UnavailableOrigin();

            var result = (await BuildService(origin: origin).StatusAsync(copy)).ShouldNotBeNull();

            result.Issues.ShouldHaveSingleItem().Message.ShouldBe("The file is not at the retained native capture source path.");
            origin.ReceivedCalls().ShouldBeEmpty();
        }
        finally
        {
            RemoveDirectoryAlias(link);
        }
    }

    // Defends against: a link loop past the depth cap being returned as the raw spelling and
    // then judged as an ordinary contained path.
    [Fact]
    public async Task Status_PathThroughLinkLoop_RefusesWithNamedLinkDepthReason()
    {
        var (a, b) = CreateLinkLoop();
        try
        {
            var file = Path.Combine(a, "plan.json");
            CanonicalPathResolver.Resolve(file).Refusal.ShouldBe(CanonicalPathResolver.RefusalKind.LinkDepthExceeded);

            var status = (await BuildService().StatusAsync(file)).ShouldNotBeNull();

            status.Found.ShouldBeFalse();
            status.Issues.ShouldContain(i => i.Message.StartsWith("Invalid file path: link resolution exceeded"));
        }
        finally
        {
            RemoveLink(a);
            RemoveLink(b);
        }
    }

    // Defends against: a capture retaining an unresolved spelling when its proposal directory
    // cannot be resolved to a physical location.
    [Fact]
    public async Task Capture_ProposalDirectoryThroughLinkLoop_RefusesInsteadOfRetainingRawPath()
    {
        var (a, b) = CreateLinkLoop();
        try
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => CaptureDraftAsync(_repoRoot, Path.Combine(a, "proposals")));

            ex.Message.ShouldStartWith("Capture refuses an unresolvable proposal directory: link resolution exceeded");
        }
        finally
        {
            RemoveLink(a);
            RemoveLink(b);
        }
    }

    // Defends against: the typed resolver refusing a not-yet-created proposal location, which
    // only genuinely missing segments may pass through.
    [Fact]
    public void CanonicalResolver_MissingSegmentsBelowAlias_KeepRemainderUnderPhysicalTarget()
    {
        var real = Path.Combine(_repoRoot, "real-proposals");
        var link = Path.Combine(_repoRoot, "proposals-alias");
        Directory.CreateDirectory(real);
        CreateDirectoryAlias(link, real);
        try
        {
            var resolved = CanonicalPathResolver.Resolve(Path.Combine(link, "not-yet", "draft.json"));

            resolved.Refusal.ShouldBeNull();
            resolved.Path.ShouldBe(Path.Combine(CanonicalPathResolver.Resolve(real).Path!, "not-yet", "draft.json"));
        }
        finally
        {
            RemoveDirectoryAlias(link);
        }
    }

    // Defends against: a segment that exists but cannot be inspected being treated as a plain,
    // unlinked directory (the old resolver swallowed the error and kept walking).
    [UninspectableSegmentFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void CanonicalResolver_UninspectableSegment_RefusesInsteadOfReturningRawPath()
    {
        var locked = Path.Combine(_repoRoot, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var resolved = CanonicalPathResolver.Resolve(Path.Combine(locked, "inner", "plan.json"));

            resolved.Refusal.ShouldBe(CanonicalPathResolver.RefusalKind.InaccessibleSegment);
            resolved.Path.ShouldBeNull();
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class UninspectableSegmentFactAttribute : FactAttribute
    {
        public UninspectableSegmentFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "An owner-inaccessible directory needs a privileged ACL edit on Windows; this edge is exercised on Unix.";
            else if (Environment.UserName == "root")
                Skip = "root bypasses POSIX search permission, so an inaccessible segment cannot be produced.";
        }
    }

    private (string A, string B) CreateLinkLoop()
    {
        var a = Path.Combine(_repoRoot, "loop-a");
        var b = Path.Combine(_repoRoot, "loop-b");
        CreateDirectoryAlias(a, b);
        CreateDirectoryAlias(b, a);
        return (a, b);
    }

    private static void RemoveLink(string link)
    {
        // A looping link fails Directory.Exists on Unix, so test the link itself, never its target.
        if (new FileInfo(link).LinkTarget is null) return;
        if (OperatingSystem.IsWindows()) Directory.Delete(link);
        else File.Delete(link);
    }

    private async Task<PlanJournal> CaptureDraftAsync(string checkoutRoot, string proposalDirectory)
    {
        var repository = new SqliteWorkItemRepository(_store, _mapper);
        await repository.SaveAsync(_mapper.Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 3, Title = "baseline", TypeName = "FixtureType", State = "FixtureState",
            Fields = new Dictionary<string, string?> { ["Fixture.Field"] = "old" },
        }));
        var snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(
            new QualifiedWorkItemKey(checkoutRoot, "fixture-connection", "fixture-binding", 42), 3)
            .SetField("Fixture.Field", "old", "intent");
        (await new SqliteRecoverableWorkItemDraftStore(_store).TrySaveAsync(draft, null)).ShouldBeTrue();
        var journal = await new SqliteDraftProposalCaptureStore(_store).CaptureAsync(
            draft, snapshot, await new FixtureOrigin(checkoutRoot).GetOriginAsync(),
            new PlanWorkspace { Organization = "acme", Project = "cache" }, proposalDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.SourcePath)!);
        await File.WriteAllTextAsync(journal.SourcePath, journal.CanonicalJson);
        return journal;
    }

    private static IPlanOriginProvider UnavailableOrigin()
    {
        var origin = Substitute.For<IPlanOriginProvider>();
        origin.GetOriginAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Current principal is unavailable."));
        return origin;
    }

    // Simulates a row written by the pre-canonical capture writer, which retained
    // Path.GetFullPath of the alias spelling. Rewrites exactly one test-owned row.
    private void RetainLegacySpelling(string digest, string legacy, string canonical)
    {
        using (var command = _store.GetConnection().CreateCommand())
        {
            command.CommandText = "UPDATE pending.proposal_journals SET source_path=@legacy WHERE digest=@digest;";
            command.Parameters.AddWithValue("@legacy", legacy);
            command.Parameters.AddWithValue("@digest", digest);
            command.ExecuteNonQuery().ShouldBe(1);
        }
        RetainedSourcePath(digest).ShouldBe(legacy);
        string.Equals(legacy, canonical, StringComparison.OrdinalIgnoreCase).ShouldBeFalse("precondition: the retained spelling differs from the canonical path");
        CanonicalPathResolver.Resolve(legacy).Path.ShouldBe(canonical, "precondition: the legacy spelling names the same physical file");
    }

    private string RetainedSourcePath(string digest)
    {
        using var command = _store.GetConnection().CreateCommand();
        command.CommandText = "SELECT source_path FROM pending.proposal_journals WHERE digest=@digest;";
        command.Parameters.AddWithValue("@digest", digest);
        return (string)command.ExecuteScalar()!;
    }

    private static void RemoveDirectoryAlias(string alias)
    {
        // Removes only the link itself; a non-recursive delete never enters the target.
        if (Directory.Exists(alias) && new DirectoryInfo(alias).LinkTarget is not null)
            Directory.Delete(alias);
    }

    private static void CreateDirectoryAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }
        // A directory symlink needs elevation or Developer Mode on Windows; a junction does not.
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", alias, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit(10_000).ShouldBeTrue("mklink /J did not exit");
        mklink.ExitCode.ShouldBe(0, output);
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

    // ── #1178 AC (a): opaque revision precondition through apply ───────────

    /// <summary>
    /// Delegates to the real ADO adapter but re-labels one item's precondition with a foreign
    /// provider kind, so a refusal arises at the real port after an earlier operation landed.
    /// </summary>
    private sealed class ForeignPreconditionWriter(IProviderWorkItemWriter inner, int foreignItemId)
        : IProviderWorkItemWriter
    {
        public Task<ProviderRevisionToken> PatchAsync(int id, IReadOnlyList<FieldChange> changes,
            ProviderRevisionPrecondition expected, CancellationToken ct = default)
            => inner.PatchAsync(id, changes,
                id == foreignItemId
                    ? new ProviderRevisionPrecondition(ProviderCapabilityCatalog.GitHubProviderKind, expected.Expected)
                    : expected,
                ct);

        public Task AddCommentAsync(int id, string text, CancellationToken ct = default)
            => inner.AddCommentAsync(id, text, ct);

        public Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
            => inner.AddLinkAsync(sourceId, targetId, adoLinkType, ct);

        public Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment,
            CancellationToken ct = default)
            => inner.AddLinkWithCommentAsync(sourceId, targetId, adoLinkType, comment, ct);

        public Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
            => inner.RemoveLinkAsync(sourceId, targetId, adoLinkType, ct);

        public Task<WorkItem> FetchAsync(int id, CancellationToken ct = default) => inner.FetchAsync(id, ct);

        public Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id,
            CancellationToken ct = default) => inner.FetchWithLinksAsync(id, ct);

        public Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
            IReadOnlyList<int> ids, CancellationToken ct = default) => inner.FetchBatchWithLinksAsync(ids, ct);

        public Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default)
            => inner.FetchChildrenAsync(parentId, ct);

        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default)
            => inner.QueryByWiqlAsync(wiql, ct);

        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default)
            => inner.QueryByWiqlAsync(wiql, top, ct);

        public Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
            => inner.FetchBatchAsync(ids, ct);
    }

    [Fact]
    public async Task Apply_PreconditionRefusedAfterEarlierWrite_IsDeterminateFailure_AndKeepsTheEarlierWrite()
    {
        var file = WritePlan(TwoBatchPlan(a: (100, 3), b: (101, 5)));
        var svc = BuildService(writer: new ForeignPreconditionWriter(_writer, foreignItemId: 101));
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _ado.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(100, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        apply.Error.ShouldBeNull();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[1].State.ShouldBe(PlanOperationState.Failed);
        apply.Operations[1].Error!.ShouldStartWith("revision-precondition-refused:");

        var status = (await svc.StatusAsync(file))!;
        status.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        var refused = status.Operations[1];
        refused.State.ShouldBe(PlanOperationState.Failed);
        refused.Diagnostics.Disposition.ShouldBe("failed");
        refused.Diagnostics.ExpectedRevision.ShouldBe(5);
        refused.Diagnostics.ObservedRevision.ShouldBeNull();
        // The journal row carries the refusal code; the shared diagnostics projection
        // allowlists known codes and does not surface this one yet, so read the raw row.
        using (var result = System.Text.Json.JsonDocument.Parse(refused.ResultJson!))
        {
            var diag = result.RootElement.GetProperty("diagnostics");
            diag.GetProperty("code").GetString().ShouldBe("revision-precondition-refused");
            diag.GetProperty("expectedRevision").GetInt32().ShouldBe(5);
            result.RootElement.TryGetProperty("rev", out _).ShouldBeFalse();
            result.RootElement.TryGetProperty("revision", out _).ShouldBeFalse();
        }

        await _ado.Received(1).PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().PatchAsync(101, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().FetchAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Batch_DigestAndJournalFormat_AreUnchangedByTheOpaquePort()
    {
        // D3: the proposal, digest and journal stay int-shaped in this slice. The digest of a
        // fixed plan and the acknowledged-revision JSON are pinned as literals.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 4, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        digest.ShouldBe(PinnedBatchDigest);
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        var row = (await svc.StatusAsync(file))!.Operations.ShouldHaveSingleItem();
        row.ResultJson.ShouldBe(PinnedBatchResultJson);
        row.Diagnostics.ExpectedRevision.ShouldBe(3);
        row.Diagnostics.ObservedRevision.ShouldBe(4);
    }

    private const string PinnedBatchDigest = "e7ed689fe4a01035670a9facac30f0a22d65be0f08dfc1c11685f8f9f93d3979";
    private const string PinnedBatchResultJson =
        """{"rev":4,"revision":4,"diagnostics":{"code":"verified","expectedRevision":3,"observedRevision":4,"fields":[{"field":"System.State","classification":"exact"}],"missingFields":[]},"fieldEvidence":[{"field":"System.State","expected":"Active","actual":"Active"}]}""";

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

    [Theory]
    [InlineData(PlanOperationState.Planned)]
    [InlineData(PlanOperationState.Applying)]
    [InlineData(PlanOperationState.Applied)]
    public async Task Apply_HtmlClosingTagWhitespace_VerifiesAndPersistsWarning(PlanOperationState initialState)
    {
        _fieldDefinitions
            .GetByReferenceNameAsync("System.Description", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(new FieldDefinition(
                "System.Description", "Description", "html", IsReadOnly: false)));
        var file = WritePlan(BatchWithFields(42, 3,
        [
            ("System.Description", "<p>Alpha</p><p>Beta</p>"),
        ]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        if (initialState != PlanOperationState.Planned)
        {
            var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
            await _journal.TryTransitionOperationAsync(digest, opId,
                PlanOperationState.Planned, PlanOperationState.Confirmed, stale);
            await _journal.TryTransitionOperationAsync(digest, opId,
                PlanOperationState.Confirmed, PlanOperationState.Applying, stale);
            if (initialState == PlanOperationState.Applied)
                await _journal.TryTransitionOperationAsync(digest, opId,
                    PlanOperationState.Applying, PlanOperationState.Applied, stale);
        }
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        var refreshed = BuildWorkItem(42, rev: 4);
        refreshed.UpdateField("System.Description", "<p>Alpha </p><p>Beta </p>");
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(refreshed);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[0].Warning.ShouldNotBeNull();
        apply.Operations[0].Warning!.ShouldContain("System.Description");
        var persisted = (await _journal.GetAsync(digest))!.Operations[0];
        persisted.State.ShouldBe(PlanOperationState.Verified);
        persisted.Warning.ShouldBe(apply.Operations[0].Warning);
        if (initialState != PlanOperationState.Planned)
            await _ado.DidNotReceive().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("System.Description")]
    [InlineData("Custom.FalsificationCriteria")]
    public async Task Apply_EntityBearingHtmlRoundTrip_JournalStateIsIdenticalAcrossHtmlFields(string field)
    {
        // AB#851: System.Description verified while Custom.FalsificationCriteria + State went
        // Indeterminate for a write that landed. Equivalent writes must journal identically.
        _fieldDefinitions
            .GetByReferenceNameAsync(field, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FieldDefinition?>(new FieldDefinition(field, field, "html", IsReadOnly: false)));
        var file = WritePlan(BatchWithFields(42, 3,
        [
            ("System.State", "Done"),
            (field, PlanOperationExecutorTests.Ab851Staged),
        ]));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        var refreshed = BuildWorkItem(42, rev: 4, state: "Done");
        refreshed.UpdateField(field, PlanOperationExecutorTests.Ab851AdoStored);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(refreshed);

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeFalse();
        var persisted = (await _journal.GetAsync(digest))!.Operations[0];
        persisted.State.ShouldBe(PlanOperationState.Verified);
        persisted.Error.ShouldBeNull();
        persisted.Warning.ShouldNotBeNull();
        persisted.Diagnostics.Code.ShouldBe("verified");
        persisted.Diagnostics.Fields.ShouldContain(f => f.Field == field && f.Classification == "canonicalized-html");
        persisted.Diagnostics.Fields.ShouldContain(f => f.Field == "System.State" && f.Classification == "exact");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RecoveryFromApplied_PreservesAcknowledgmentWithTerminalDiagnostics(bool readbackFails)
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, timestamp);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, timestamp);
        await _journal.TryRecordAppliedAsync(digest, opId,
            """{"rev":4,"receipt":{"token":"private-ack","flags":[true,null,7]}}""", timestamp);
        if (readbackFails)
            _ado.FetchAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));
        else
            _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 5, state: "Active"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));
        var status = (await svc.StatusAsync(file))!;
        apply.Failed.ShouldBe(readbackFails);
        status.Operations[0].ResultJson.ShouldBe(apply.Operations[0].ResultJson);
        var row = status.Operations[0];
        row.State.ShouldBe(readbackFails ? PlanOperationState.Indeterminate : PlanOperationState.Verified);
        using var document = JsonDocument.Parse(row.ResultJson!);
        document.RootElement.GetProperty("rev").GetInt32().ShouldBe(4);
        var receipt = document.RootElement.GetProperty("receipt");
        receipt.GetProperty("token").GetString().ShouldBe("private-ack");
        receipt.GetProperty("flags")[2].GetInt32().ShouldBe(7);
        row.Diagnostics.Code.ShouldBe(readbackFails ? "readback-unavailable" : "verified");
        row.Diagnostics.ExpectedRevision.ShouldBe(3);
        row.Diagnostics.ObservedRevision.ShouldBe(readbackFails ? null : 5);
        row.Diagnostics.Summary.ShouldNotContain("private-ack");
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_DeleteReadbackUnavailable_PreservesAcknowledgmentWithTerminalDiagnostics()
    {
        var file = WritePlan(DeletePlan(workItemId: 42, rev: 3));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));
        apply.Failed.ShouldBeTrue();
        var status = (await svc.StatusAsync(file))!;
        var row = status.Operations[0];
        row.State.ShouldBe(PlanOperationState.Indeterminate);
        row.ResultJson.ShouldBe(apply.Operations[0].ResultJson);
        using var document = JsonDocument.Parse(row.ResultJson!);
        document.RootElement.GetProperty("deleted").GetInt32().ShouldBe(42);
        row.Diagnostics.Code.ShouldBe("readback-unavailable");
        row.Diagnostics.ExpectedRevision.ShouldBe(3);
        row.Diagnostics.ObservedRevision.ShouldBeNull();
        await _revisionBound.Received(1).DeleteAtRevisionAsync(42, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AppendComment_LostReceiptPersistsIndeterminateAndNeverReplaysPost()
    {
        var setup = await CreateDiscussionAppendProposalAsync();
        setup.Writer.AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection lost after request"));

        var first = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        first.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Indeterminate);
        var persisted = (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem();
        persisted.State.ShouldBe(PlanOperationState.Indeterminate);
        persisted.ResultJson.ShouldBeNull();
        await setup.Reader.DidNotReceive().ReadDiscussionCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        var resumed = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        resumed.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Indeterminate);
        await setup.Writer.Received(1).AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AppendComment_ReadsOnlyReceiptCommentAndVerifiesPersistedEffect()
    {
        var setup = await CreateDiscussionAppendProposalAsync();
        setup.Writer.AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DiscussionPostReceipt(42, "45", setup.Body, "html")));
        setup.Reader.ReadDiscussionCommentAsync(42, "45", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DiscussionComment?>(new DiscussionComment(
                "45", "1", new DiscussionAuthor("author-1", "Reviewer"),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, setup.Body, "html")));

        var apply = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        apply.Failed.ShouldBeFalse();
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Verified);
        var persisted = (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem();
        persisted.State.ShouldBe(PlanOperationState.Verified);
        using var result = JsonDocument.Parse(persisted.ResultJson!);
        result.RootElement.GetProperty("commentId").GetString().ShouldBe("45");
        result.RootElement.GetProperty("body").GetString().ShouldBe(setup.Body);
        result.RootElement.GetProperty("format").GetString().ShouldBe("html");
        await setup.Reader.Received(1).ReadDiscussionCommentAsync(42, "45", Arg.Any<CancellationToken>());
        await setup.Reader.DidNotReceive().ReadDiscussionAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AppendComment_MissingReceiptCommentStaysIndeterminateWithoutListSearch()
    {
        var setup = await CreateDiscussionAppendProposalAsync();
        setup.Writer.AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DiscussionPostReceipt(42, "46", setup.Body, "html")));
        setup.Reader.ReadDiscussionCommentAsync(42, "46", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DiscussionComment?>(null));

        var apply = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Indeterminate);
        var persisted = (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem();
        persisted.State.ShouldBe(PlanOperationState.Indeterminate);
        await setup.Reader.Received(1).ReadDiscussionCommentAsync(42, "46", Arg.Any<CancellationToken>());
        await setup.Reader.DidNotReceive().ReadDiscussionAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await setup.Writer.Received(1).AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("wrong-receipt-body")]
    [InlineData("wrong-receipt-target")]
    [InlineData("wrong-receipt-format")]
    [InlineData("deleted-comment")]
    [InlineData("wrong-readback-body")]
    public async Task Apply_AppendComment_ContradictoryReceiptOrReadbackFailsWithoutReplay(string mismatch)
    {
        var setup = await CreateDiscussionAppendProposalAsync();
        var receiptBody = mismatch == "wrong-receipt-body" ? "<p>different</p>\n" : setup.Body;
        var receiptWorkItemId = mismatch == "wrong-receipt-target" ? 43 : 42;
        var receiptFormat = mismatch == "wrong-receipt-format" ? "plain" : "html";
        setup.Writer.AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DiscussionPostReceipt(receiptWorkItemId, "47", receiptBody, receiptFormat)));
        if (mismatch is not ("wrong-receipt-body" or "wrong-receipt-target" or "wrong-receipt-format"))
        {
            setup.Reader.ReadDiscussionCommentAsync(42, "47", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<DiscussionComment?>(new DiscussionComment(
                    "47", "1", new DiscussionAuthor("author-1", "Reviewer"),
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    mismatch == "deleted-comment", mismatch == "wrong-readback-body" ? "<p>different</p>\n" : setup.Body,
                    "html")));
        }

        var apply = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        var expectedState = mismatch == "wrong-receipt-target"
            ? PlanOperationState.Indeterminate
            : PlanOperationState.Failed;
        apply.Operations.ShouldHaveSingleItem().State.ShouldBe(expectedState);
        (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem().State.ShouldBe(expectedState);
        if (mismatch is "wrong-receipt-body" or "wrong-receipt-target" or "wrong-receipt-format")
            await setup.Reader.DidNotReceive().ReadDiscussionCommentAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await setup.Writer.Received(1).AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_AppendComment_ChangedIdentityOrBenchRefusesBeforeTransport(bool changeIdentity)
    {
        var setup = await CreateDiscussionAppendProposalAsync();
        if (changeIdentity)
            setup.Authentication.Identity = setup.Authentication.Identity with { IdentityId = "identity-2" };
        else
            await setup.Benches.AddSelectorAsync(setup.BenchId, BenchSelector.ForItem(43));

        var apply = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));

        apply.Failed.ShouldBeTrue();
        var expectedRefusal = changeIdentity
            ? "The bound identity changed since the discussion append was reviewed."
            : "The saved Bench contents changed since the discussion append was reviewed.";
        apply.Operations.ShouldHaveSingleItem().Error.ShouldBe(expectedRefusal);
        (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem().Error.ShouldBe(expectedRefusal);
        await setup.Writer.DidNotReceive().AppendDiscussionCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AppendComment_CancellationAfterAdmissionDoesNotRetry()
    {
        var setup = await CreateDiscussionAppendProposalAsync(withClock: true);
        using var cancellation = new CancellationTokenSource();
        setup.Writer.AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.ArgAt<CancellationToken>(2);
                cancellation.Cancel();
                return Task.FromCanceled<DiscussionPostReceipt>(token);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => setup.Service.ApplyAsync(
            setup.File, setup.Digest, Authorize(setup.Digest), cancellation.Token));

        (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Applying);
        setup.Clock!.Advance(TimeSpan.FromMinutes(6));
        var resumed = await setup.Service.ApplyAsync(setup.File, setup.Digest, Authorize(setup.Digest));
        resumed.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Indeterminate);
        (await _journal.GetAsync(setup.Digest))!.Operations.ShouldHaveSingleItem().State.ShouldBe(PlanOperationState.Indeterminate);
        await setup.Writer.Received(1).AppendDiscussionCommentAsync(42, setup.Body, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Diagnostics_RevisionPreconditionRefusalRetainsSafeNoWriteClassification()
    {
        var operation = new PlanJournalOperation
        {
            Ordinal = 0,
            OpId = "op",
            Kind = PlanOperationKind.Batch,
            State = PlanOperationState.Failed,
            RequestJson = """{"expectedRevision":3}""",
            ResultJson = """{"diagnostics":{"code":"revision-precondition-refused","expectedRevision":3,"observedRevision":null,"failureDetail":"ForeignProvider"}}""",
            Error = "revision refusal included opaque-secret-token",
        };

        var diagnostics = operation.Diagnostics;

        diagnostics.Disposition.ShouldBe("failed");
        diagnostics.Code.ShouldBe("revision-precondition-refused");
        diagnostics.ExpectedRevision.ShouldBe(3);
        diagnostics.ObservedRevision.ShouldBeNull();
        diagnostics.Summary.ShouldBe("revision precondition refused before transport; nothing was sent");
        diagnostics.Summary.ShouldNotContain("opaque-secret-token");
        diagnostics.Summary.ShouldNotContain("ForeignProvider");
    }

    [Theory]
    [InlineData("\"legacy acknowledgment\"")]
    [InlineData("opaque legacy acknowledgment")]
    public async Task Apply_RecoveryFromApplied_PreservesOpaqueLegacyAcknowledgment(string original)
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        var timestamp = DateTimeOffset.UtcNow;
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, timestamp);
        await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, timestamp);
        await _journal.TryRecordAppliedAsync(digest, opId, original, timestamp);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(BuildWorkItem(42, rev: 5, state: "Active"));

        var result = await svc.ApplyAsync(file, digest, Authorize(digest));
        result.Failed.ShouldBeFalse();
        var row = (await svc.StatusAsync(file))!.Operations[0];
        using var document = JsonDocument.Parse(row.ResultJson!);
        document.RootElement.GetProperty("acknowledgedResultJson").GetString().ShouldBe(original);
        document.RootElement.TryGetProperty("rev", out _).ShouldBeFalse();
        row.Diagnostics.Code.ShouldBe("verified");
        row.Diagnostics.ObservedRevision.ShouldBe(5);
        row.Diagnostics.Summary.ShouldNotContain(original);
        await _ado.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
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

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    public async Task Reconcile_RejectedCreateFamily_RequiresEveryMatchingSiblingToBeRejected(
        bool secondMemberMatchesOriginalIntent, bool secondMemberRejected, bool secondMemberHasStructuredCorrelation)
        => await ReconcileRejectedCreateFamilyScenario(
            secondMemberMatchesOriginalIntent, secondMemberRejected, secondMemberHasStructuredCorrelation);

    [Theory]
    [InlineData(null)]
    [InlineData("principal")]
    [InlineData("attachment")]
    [InlineData("raw-root")]
    public async Task Reconcile_RejectedCreateFamily_AliasedWorktreePreservesExactAuthority(string? changedAuthority)
    {
        if (OperatingSystem.IsWindows())
        {
            await ReconcileRejectedCreateFamilyScenario(
                true, true, true, Path.Combine(_repoRoot, ".", ".twig"), changedAuthority);
            return;
        }

        var alias = Path.Combine(_repoRoot, "linked-root");
        Directory.CreateSymbolicLink(alias, _repoRoot);
        try
        {
            await ReconcileRejectedCreateFamilyScenario(
                true, true, true, Path.Combine(alias, ".twig"), changedAuthority);
        }
        finally
        {
            RemoveDirectoryAlias(alias);
        }
    }

    private async Task ReconcileRejectedCreateFamilyScenario(
        bool secondMemberMatchesOriginalIntent, bool secondMemberRejected, bool secondMemberHasStructuredCorrelation,
        string? aliasedStartDir = null, string? changedAuthority = null)
    {
        var identity = StagedIdentity.FromGuid(Guid.Parse("01947f00-0000-7000-8000-000000000041"));
        InitializeGitRepository(_repoRoot);
        var startDir = aliasedStartDir ?? _repoRoot;
        WorktreeAnchorDetector.TryDetect(startDir, out var anchor, out var anchorFailure)
            .ShouldBeTrue($"the real Git fixture must have a stable anchor: {anchorFailure}");
        var worktreeRoot = anchor.WorktreeRoot;
        CanonicalPathResolver.Resolve(startDir).Refusal.ShouldBeNull();
        if (aliasedStartDir is not null)
        {
            startDir.ShouldNotBe(CanonicalPathResolver.Resolve(startDir).Path,
                "the control must actually enter through an alias (lexical on Windows, symbolic on Unix)");
            WorktreeAnchorDetector.TryDetect(_repoRoot, out var directAnchor, out _).ShouldBeTrue();
            anchor.WorktreeRoot.ShouldBe(directAnchor.WorktreeRoot,
                "aliased and direct entry must discover the same physical worktree root");
            foreach (var (aliasedDirectory, directDirectory) in new[]
            {
                (anchor.GitCommonDir, directAnchor.GitCommonDir),
                (anchor.WorktreeGitDir, directAnchor.WorktreeGitDir),
            })
            {
                Directory.Exists(aliasedDirectory).ShouldBeTrue();
                Directory.Exists(directDirectory).ShouldBeTrue();
                var aliasedResolution = CanonicalPathResolver.Resolve(aliasedDirectory);
                var directResolution = CanonicalPathResolver.Resolve(directDirectory);
                aliasedResolution.Refusal.ShouldBeNull();
                directResolution.Refusal.ShouldBeNull();
                aliasedResolution.Path.ShouldNotBeNull();
                directResolution.Path.ShouldNotBeNull();
                aliasedResolution.Path.ShouldBe(directResolution.Path,
                    "Git metadata directory spellings may differ but must name the same physical directory");
            }
        }
        await _config.SaveSplitAsync(_paths);
        using var workspaceStore = new SqliteCacheStore($"Data Source={_paths.DbPath}");
        var workspaceJournal = new SqlitePlanJournalRepository(workspaceStore);
        var file = WritePlan(PublishSeedPlan(identity, expectedFingerprint: "fixture-seed"));
        var registryPath = Path.Combine(_repoRoot, "rejected-family-system.db");
        var originProvider = Substitute.For<IPlanOriginProvider>();
        var origin = (await new FixtureOrigin(worktreeRoot).GetOriginAsync()) with
        {
            WorktreeFingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor),
        };
        originProvider.GetOriginAsync(Arg.Any<CancellationToken>()).Returns(origin);
        var planService = BuildService(
            journal: workspaceJournal, origin: originProvider,
            systemRegistryPath: registryPath, startDir: startDir);
        var validation = await planService.ValidateAsync(file);
        validation.IsValid.ShouldBeTrue();
        var digest = validation.Digest!;
        (await workspaceJournal.ImportAsync(validation.Plan!, validation.CanonicalJson!, digest, file,
            DateTimeOffset.UtcNow, origin: origin)).ShouldNotBeNull();
        var operation = (await workspaceJournal.GetAsync(digest))!.Operations.Single();
        var now = DateTimeOffset.UtcNow;
        (await workspaceJournal.TryTransitionOperationAsync(digest, operation.OpId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, now)).ShouldBeTrue();
        (await workspaceJournal.TryTransitionOperationAsync(digest, operation.OpId,
            PlanOperationState.Confirmed, PlanOperationState.Applying, now)).ShouldBeTrue();
        await workspaceJournal.SaveOperationErrorAsync(digest, operation.OpId, "Original create outcome is unknown.",
            PlanOperationState.Indeterminate, now);

        var workspaceIntents = new SqlitePublishIntentRepository(workspaceStore);
        var intent = await workspaceIntents.RecordIntentAsync(identity, "Rejected family seed", "Task");
        var independentIdentity = StagedIdentity.New();
        var independentIntent = await workspaceIntents.RecordIntentAsync(
            independentIdentity, "Independent seed", "Task");
        _publishIntent.GetIntentAsync(identity, Arg.Any<CancellationToken>()).Returns(intent);

        using var native = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        (await native.EnsureBindingConnectionAsync(origin.ConnectionRef, "acme", "cache", CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
        (await native.UpsertWorktreeAsync(origin.WorktreeFingerprint, origin.ConnectionRef, origin.WorktreeRoot))
            .IsSuccess.ShouldBeTrue();
        var registeredAt = DateTimeOffset.UtcNow;
        (await native.InsertIdentityAsync(new IdentityRow(
            origin.IdentityId, "fixture", origin.TenantId, origin.ObjectId, origin.Issuer,
            "login.microsoftonline.com", origin.CredentialRef, null, registeredAt, registeredAt,
            origin.Method))).IsSuccess.ShouldBeTrue();
        (await native.InsertOrGetBindingAsync(new BindingRow(
            origin.BindingId, origin.ConnectionRef, origin.IdentityId, origin.BindingRevision,
            registeredAt, registeredAt))).IsSuccess.ShouldBeTrue();

        var correlation = new SeedPublishCorrelation(identity, intent.RecordedAt);
        var payload = "[{\"op\":\"add\",\"path\":\"/fields/System.Title\",\"value\":\"Rejected family seed\"},"
            + "{\"op\":\"add\",\"path\":\"/fields/System.Description\",\"value\":\""
            + correlation.DescriptionMarkerHtml + "\"}]";
        var request = new ConnectionRemoteWriteRequest(
            "workitem-create",
            "POST",
            "https://dev.azure.com/acme/cache/_apis/wit/workitems/$Task?api-version=7.1",
            payload,
            null,
            correlation);
        var binding = new ResolvedConnectionBinding(
            new IdentityBinding(origin.BindingId, origin.ConnectionRef, origin.IdentityId, origin.BindingRevision),
            new AuthenticationIdentity(origin.IdentityId, "fixture", origin.TenantId, origin.ObjectId,
                origin.Issuer, "login.microsoftonline.com", origin.CredentialRef, null, origin.Method),
            origin.WorktreeRoot,
            origin.SelectionSource,
            origin.SelectionRevision,
            new ConnectionOperationSnapshot(
                "acme", "cache", "", origin.WorktreeFingerprint, origin.AttachmentRevision,
                "/fixture/config", "/fixture/preferences", "ascii", "default"));
        ConnectionRemoteWriteAdmission.SeedCreateTitle(request.Payload).ShouldBe(intent.Title);
        ConnectionRemoteWriteAdmission.SeedCreateType(request.Target).ShouldBe(intent.TypeName);
        ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(request.Payload, correlation).ShouldBeTrue();
        ConnectionRemoteWriteAdmission.IsWithinAuthority(binding, new Uri(request.Target)).ShouldBeTrue();
        for (var index = 0; index < 2; index++)
        {
            var memberCorrelation = index == 1 && !secondMemberMatchesOriginalIntent
                ? new SeedPublishCorrelation(identity, intent.RecordedAt.AddTicks(1))
                : correlation;
            var memberRequest = request with
            {
                SeedCorrelation = index == 1 && !secondMemberHasStructuredCorrelation
                    ? null
                    : memberCorrelation,
            };
            var nativeIntent = new ConnectionRemoteWriteIntent(
                1, Guid.NewGuid().ToString("N"),
                ConnectionRemoteWriteAdmission.RequestDigest(binding, memberRequest),
                origin.WorktreeFingerprint, binding, memberRequest, now.AddSeconds(index));
            (await native.BeginConnectionRemoteWriteAsync(nativeIntent)).IsSuccess.ShouldBeTrue();

            var response = new ConnectionRemoteWriteResponse(
                index == 1 && !secondMemberRejected ? 500 : 400,
                "{\"typeKey\":\"WorkItemFieldInvalidException\",\"requestId\":\"fixture-" + index + "\"}",
                "fixture-" + index,
                null);
            var kind = ConnectionRemoteWriteAdmission.ClassifyAcknowledgement(memberRequest, response);
            kind.ShouldBe(index == 1 && !secondMemberRejected ? null : "rejected",
                "the existing native classifier must prove each member's actual response classification");
            var observation = new ConnectionRemoteWriteObservation(
                1, Guid.NewGuid().ToString("N"), nativeIntent.IntentId, nativeIntent.RequestDigest,
                response, now.AddSeconds(index + 1));
            var nativeReceipt = kind is null ? null : new ConnectionRemoteWriteReceipt(
                1, Guid.NewGuid().ToString("N"), nativeIntent.IntentId, nativeIntent.RequestDigest, kind,
                observation.ObservationId,
                JsonSerializer.Serialize(response, Twig.Infrastructure.Serialization.TwigJsonContext.Default.ConnectionRemoteWriteResponse),
                binding, null, null, now.AddSeconds(index + 1));
            (await native.ObserveConnectionRemoteWriteAsync(nativeIntent, observation, nativeReceipt)).IsSuccess.ShouldBeTrue();
        }

        var histories = await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint);
        histories.IsSuccess.ShouldBeTrue();
        histories.Value.Count.ShouldBe(2);
        histories.Value[0].Receipt?.Kind.ShouldBe("rejected");
        histories.Value[0].Observations.ShouldHaveSingleItem();
        histories.Value[1].Observations.ShouldHaveSingleItem();
        histories.Value.All(history =>
            history.Intent.Fingerprint == origin.WorktreeFingerprint
            && history.Intent.Request.EffectKind == request.EffectKind
            && history.Intent.Request.Method == request.Method
            && history.Intent.Request.Target == request.Target
            && history.Intent.Origin.Binding.BindingId == origin.BindingId
            && history.Intent.Origin.Binding.ConnectionRef == origin.ConnectionRef
            && history.Intent.Origin.Binding.IdentityId == origin.IdentityId
            && (history.Intent.Request.SeedCorrelation is { } captured
                ? captured.Identity == identity
                : history.Intent.Request.EffectKind == "workitem-create"))
            .ShouldBeTrue("both native creates must be in the exact staged identity family");
        var secondHistory = histories.Value[1];
        if (secondMemberHasStructuredCorrelation)
            secondHistory.Intent.Request.SeedCorrelation!.IntentRecordedAt.ShouldBe(
                secondMemberMatchesOriginalIntent ? intent.RecordedAt : intent.RecordedAt.AddTicks(1));
        else
        {
            secondHistory.Intent.Request.SeedCorrelation.ShouldBeNull();
            ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(
                secondHistory.Intent.Request.Payload, correlation).ShouldBeTrue(
                "the unstructured sibling still carries the exact original staged identity marker");
        }
        ConnectionRemoteWriteAdmission.ClassifyAcknowledgement(
            secondHistory.Intent.Request, secondHistory.Observations[0].Response)
            .ShouldBe(secondMemberRejected ? "rejected" : null);
        var unknown = (await workspaceJournal.GetAsync(digest))!.Operations.Single();
        unknown.State.ShouldBe(PlanOperationState.Indeterminate);
        unknown.OutcomeReceipt.ShouldBeNull();
        (await workspaceIntents.GetIntentAsync(identity))!.IsOpen.ShouldBeTrue();
        var originalJournal = (await workspaceJournal.GetAsync(digest))!;
        originalJournal.Origin.ShouldBe(origin);
        (await originProvider.GetOriginAsync()).ShouldBe(originalJournal.Origin,
            "the family controls must reach their intended guard, not fail on fixture authority");
        WorktreeAnchorDetector.TryDetect(startDir, out var currentAnchor, out var currentAnchorFailure)
            .ShouldBeTrue($"the originating anchor must still be available: {currentAnchorFailure}");
        currentAnchor.ShouldBe(anchor, "fixture setup must not change the originating Git anchor");
        origin.WorktreeRoot.ShouldBe(currentAnchor.WorktreeRoot,
            "the fixture must compose origins from the same detected root as the bound runtime");
        origin.WorktreeFingerprint.ShouldBe(WorktreeFingerprintProvider.CanonicalJson(currentAnchor));
        binding.WorktreeRoot.ShouldBe(origin.WorktreeRoot);
        binding.Operation.WorktreeFingerprint.ShouldBe(origin.WorktreeFingerprint);
        binding.Operation.AttachmentRevision.ShouldBe(origin.AttachmentRevision);
        binding.Binding.Revision.ShouldBe(origin.BindingRevision);
        binding.SelectionRevision.ShouldBe(origin.SelectionRevision);

        if (changedAuthority is not null)
        {
            var changed = changedAuthority switch
            {
                "principal" => origin with { ObjectId = origin.ObjectId + "-changed" },
                "attachment" => origin with { AttachmentRevision = origin.AttachmentRevision + 1 },
                "raw-root" => origin with { WorktreeRoot = Path.GetDirectoryName(startDir)! },
                _ => throw new ArgumentOutOfRangeException(nameof(changedAuthority)),
            };
            changed.ShouldNotBe(origin);
            if (changedAuthority == "raw-root")
            {
                CanonicalPathResolver.Resolve(changed.WorktreeRoot).Path.ShouldBe(origin.WorktreeRoot,
                    "the rejected spelling must still name the same physical root");
            }
            originProvider.GetOriginAsync(Arg.Any<CancellationToken>()).Returns(changed);
            var refused = await planService.ReconcileAsync(
                file, digest, operation.OpId, PlanOutcomeKind.RejectedCreateFamily,
                Authorize(digest) with { Rationale = "Preserve the original exact authority." });
            refused.Settled.ShouldBeFalse();
            refused.Receipt.ShouldBeNull();
            refused.Error.ShouldBe(
                "The original proposal authority or attached worktree changed; negative evidence cannot acquire a different actor.");
            var retained = (await workspaceJournal.GetAsync(digest))!;
            retained.Origin.ShouldBe(originalJournal.Origin);
            retained.CanonicalJson.ShouldBe(originalJournal.CanonicalJson);
            retained.Operations.Single().ShouldBe(unknown);
            (await workspaceIntents.GetIntentAsync(identity)).ShouldBe(intent);
            var retainedHistories = await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint);
            retainedHistories.IsSuccess.ShouldBeTrue();
            retainedHistories.Value.Select(history => JsonSerializer.Serialize(
                    history, Twig.Infrastructure.Serialization.TwigJsonContext.Default.ConnectionRemoteWriteHistory))
                .ShouldBe(histories.Value.Select(history => JsonSerializer.Serialize(
                    history, Twig.Infrastructure.Serialization.TwigJsonContext.Default.ConnectionRemoteWriteHistory)),
                    "authority refusal must preserve every recorded native intent, observation, and receipt");
            return;
        }

        if (secondMemberMatchesOriginalIntent && secondMemberRejected)
        {
            ExecuteJournalSql(workspaceStore, """
                CREATE TEMP TRIGGER fail_rejected_family_receipt
                BEFORE INSERT ON pending.proposal_receipts
                WHEN NEW.kind = 'RejectedCreateFamily'
                BEGIN SELECT RAISE(ABORT, 'simulated local receipt write failure'); END;
                """);
            var interrupted = await planService.ReconcileAsync(
                file, digest, operation.OpId, PlanOutcomeKind.RejectedCreateFamily,
                Authorize(digest) with { Rationale = "Verify failed local disposition remains unresolved." });
            interrupted.Settled.ShouldBeFalse();
            interrupted.Error.ShouldNotBeNull();
            interrupted.Error.ShouldContain("simulated local receipt write failure");
            (await workspaceJournal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
            (await workspaceIntents.GetIntentAsync(identity))!.IsOpen.ShouldBeTrue();
            ExecuteJournalSql(workspaceStore, "DROP TRIGGER fail_rejected_family_receipt;");
        }

        var result = await planService.ReconcileAsync(
            file, digest, operation.OpId, PlanOutcomeKind.RejectedCreateFamily,
            Authorize(digest) with { Rationale = "Test the bounded rejected create-family reconciliation." });

        if (!secondMemberMatchesOriginalIntent || !secondMemberRejected)
        {
            result.Settled.ShouldBeFalse();
            var expectedError = !secondMemberHasStructuredCorrelation
                ? "A native create carries the original staged identity marker without matching structured correlation; preserve the unknown outcome."
                : !secondMemberMatchesOriginalIntent
                    ? "The exact staged identity has a native create outside the original intent correlation; preserve the unknown outcome."
                    : "Every admitted create in the exact correlation family must have one classifier-backed definitive rejection; an unknown or positive sibling remains unresolved.";
            result.Error.ShouldBe(expectedError);
            result.Receipt.ShouldBeNull();
            (await workspaceJournal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBeNull();
            (await workspaceIntents.GetIntentAsync(identity))!.IsOpen.ShouldBeTrue();
            (await workspaceIntents.GetOpenIntentsAsync()).ShouldContain(open => open.Identity == identity);
            return;
        }

        result.Settled.ShouldBeTrue(
            $"a complete valid rejected create family should reconcile the original unknown proposal; refusal: {result.Error}");
        result.Receipt.ShouldNotBeNull();
        var validReceipt = result.Receipt!;
        validReceipt.Kind.ShouldBe(PlanOutcomeKind.RejectedCreateFamily);
        var storedReceiptJson = JsonSerializer.Serialize(
            validReceipt, Twig.Infrastructure.Serialization.TwigJsonContext.Default.PlanOutcomeReceipt);
        var persistedProof = JsonSerializer.Deserialize(validReceipt.EvidenceJson,
            Twig.Infrastructure.Serialization.TwigJsonContext.Default.RejectedCreateFamilyProof);
        persistedProof.ShouldNotBeNull();
        var validProof = persistedProof!;
        var originalOrigin = validReceipt.Origin!;
        var changedOrigin = originalOrigin with
        {
            SelectionSource = originalOrigin.SelectionSource + "-tampered",
        };
        var changedRecordedAt = intent.RecordedAt.AddTicks(1);
        var changedRecordedAtText = changedRecordedAt.ToString("o", CultureInfo.InvariantCulture);
        var changedMembers = validProof.Members.Select(member =>
        {
            var originalCorrelation = member.Intent.Request.SeedCorrelation!;
            var changedCorrelation = originalCorrelation with { IntentRecordedAt = changedRecordedAt };
            var changedRequest = member.Intent.Request with
            {
                Payload = member.Intent.Request.Payload.Replace(
                    originalCorrelation.DescriptionMarkerHtml,
                    changedCorrelation.DescriptionMarkerHtml,
                    StringComparison.Ordinal),
                SeedCorrelation = changedCorrelation,
            };
            var changedRequestDigest = ConnectionRemoteWriteAdmission.RequestDigest(
                member.Intent.Origin, changedRequest);
            return member with
            {
                Intent = member.Intent with
                {
                    Request = changedRequest,
                    RequestDigest = changedRequestDigest,
                },
                Observation = member.Observation with { RequestDigest = changedRequestDigest },
                Receipt = member.Receipt with { RequestDigest = changedRequestDigest },
            };
        }).ToArray();
        var changedRecordedAtReceipt = validReceipt with
        {
            PublishIntentRecordedAt = changedRecordedAtText,
            EvidenceJson = JsonSerializer.Serialize(
                validProof with
                {
                    PublishIntentRecordedAt = changedRecordedAtText,
                    Members = changedMembers,
                },
                Twig.Infrastructure.Serialization.TwigJsonContext.Default.RejectedCreateFamilyProof),
        };
        var tamperedReceipts = new[]
        {
            validReceipt with { Digest = digest + "-tampered" },
            validReceipt with { OpId = operation.OpId + "-tampered" },
            validReceipt with
            {
                RequestJson = validReceipt.RequestJson[..^1] + ",\"unexpected\":true}",
            },
            validReceipt with { Origin = changedOrigin, AuthorizingOrigin = changedOrigin },
            changedRecordedAtReceipt,
        };
        foreach (var tamperedReceipt in tamperedReceipts)
        {
            RejectedCreateFamilyReceiptValidator.IsValid(tamperedReceipt, identity).ShouldBeTrue(
                "the proof remains valid so the test isolates binding to the immutable journal row");
            var tamperedJson = JsonSerializer.Serialize(
                tamperedReceipt, Twig.Infrastructure.Serialization.TwigJsonContext.Default.PlanOutcomeReceipt);
            ExecuteJournalSql(workspaceStore, $"""
                UPDATE pending.proposal_receipts SET receipt_json = '{tamperedJson.Replace("'", "''", StringComparison.Ordinal)}'
                WHERE digest = '{digest}' AND op_id = '{operation.OpId}';
                """);
            (await workspaceJournal.GetAsync(digest))!.Operations.Single().OutcomeReceipt.ShouldBe(tamperedReceipt);

            var tamperedResult = await planService.ReconcileAsync(
                file, digest, operation.OpId, PlanOutcomeKind.RejectedCreateFamily,
                Authorize(digest) with { Rationale = "Reject a receipt not bound to its original journal row." });
            tamperedResult.Settled.ShouldBeFalse(
                "idempotent reconciliation must not settle from receipt JSON that differs from its immutable journal row");
            tamperedResult.Receipt.ShouldBeNull();

            ExecuteJournalSql(workspaceStore, $"""
                UPDATE pending.proposal_receipts SET receipt_json = '{storedReceiptJson.Replace("'", "''", StringComparison.Ordinal)}'
                WHERE digest = '{digest}' AND op_id = '{operation.OpId}';
                """);
        }
        var malformedReceipt = validReceipt with
        {
            EvidenceJson = JsonSerializer.Serialize(
                validProof with { Members = [null!] },
                Twig.Infrastructure.Serialization.TwigJsonContext.Default.RejectedCreateFamilyProof),
        };
        RejectedCreateFamilyReceiptValidator.IsValid(malformedReceipt, identity).ShouldBeFalse(
            "a malformed nested proof must be rejected, not crash a diagnostic or admission read");
        var closedIntent = await workspaceIntents.GetIntentAsync(identity);
        closedIntent.ShouldNotBeNull();
        closedIntent.RecordedAt.ShouldBe(intent.RecordedAt);
        await Should.ThrowAsync<InvalidOperationException>(
            () => workspaceIntents.RecordIntentAsync(identity, "Rejected family seed", "Task"));
        var requestAfterSettlement = request;
        var nativeHistoryBeforeReuse = (await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint)).Value;
        await Should.ThrowAsync<InvalidOperationException>(
            () => ConnectionRemoteWriteAdmission.BeginWithWorkspaceStoreAsync(
                registryPath, binding, requestAfterSettlement, workspaceStore));
        (await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint)).Value.Count
            .ShouldBe(nativeHistoryBeforeReuse.Count, "a settled identity must be refused before a new native intent is admitted");
        (await workspaceIntents.GetIntentAsync(identity)).ShouldNotBeNull(
            "the closed disposition must not make the historical intent unreadable");

        var laterIdentity = StagedIdentity.New();
        var laterIntent = await workspaceIntents.RecordIntentAsync(
            laterIdentity, "Later independent seed", "Task");
        var laterCorrelation = new SeedPublishCorrelation(laterIdentity, laterIntent.RecordedAt);
        var laterRequest = request with
        {
            Payload = "[{\"op\":\"add\",\"path\":\"/fields/System.Title\",\"value\":\"Later independent seed\"},"
                + "{\"op\":\"add\",\"path\":\"/fields/System.Description\",\"value\":\""
                + laterCorrelation.DescriptionMarkerHtml + "\"}]",
            SeedCorrelation = laterCorrelation,
        };
        await Should.ThrowAsync<InvalidOperationException>(
            () => ConnectionRemoteWriteAdmission.BeginWithWorkspaceStoreAsync(
                registryPath, binding, laterRequest with { EffectKind = "workitem-patch", Method = "PATCH" }, workspaceStore));
        (await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint)).Value.Count
            .ShouldBe(nativeHistoryBeforeReuse.Count, "seed correlation must not authorize a different mutation kind");
        var laterAdmission = await ConnectionRemoteWriteAdmission.BeginWithWorkspaceStoreAsync(
            registryPath, binding, laterRequest, workspaceStore);
        laterAdmission.ShouldNotBeNull();
        var historiesAfterIndependentAdmission =
            (await native.ReadConnectionRemoteWritesAsync(origin.WorktreeFingerprint)).Value;
        historiesAfterIndependentAdmission.Count.ShouldBe(nativeHistoryBeforeReuse.Count + 1);
        historiesAfterIndependentAdmission[^1].Intent.Request.SeedCorrelation!.Identity.ShouldBe(laterIdentity);
        var remainingOpen = await workspaceIntents.GetOpenIntentsAsync();
        remainingOpen.Count.ShouldBe(2);
        remainingOpen.ShouldContain(open =>
            open.Identity == independentIdentity && open.RecordedAt == independentIntent.RecordedAt);
        remainingOpen.ShouldContain(open =>
            open.Identity == laterIdentity && open.RecordedAt == laterIntent.RecordedAt);
        remainingOpen.ShouldNotContain(open => open.Identity == identity);
    }

    private static string FindGitRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
        throw new InvalidOperationException("The test checkout root could not be located.");
    }

    private static void InitializeGitRepository(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start git init for the isolated admission fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5_000) || process.ExitCode != 0)
            throw new InvalidOperationException($"Could not initialize isolated worktree: {stdout}{stderr}");
    }

    private static void ExecuteJournalSql(SqliteCacheStore store, string sql)
    {
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    // ── AB#832: replaced plan files ────────────────────────────────────────
    //
    // Plan files are single-use, but nothing outside twig enforces that: two sessions sharing
    // one plans directory can compute the same "next free sequence" and write the same path.
    // Because the journal is keyed by digest, an overwritten path silently resolves to
    // whichever transaction matches the bytes now on disk. These tests pin both observable
    // shapes of that clobber, and the two honest cases they must not swallow.

    /// <summary>
    /// The window observed first-hand on 2026-08-28: the file was overwritten, but the
    /// replacing document had not been previewed yet. Status used to return null here, which
    /// the CLI renders as a bare <c>found:false</c> — indistinguishable from "you never
    /// previewed this plan", and so the destroyed audit trail read as a missing one.
    /// </summary>
    [Fact]
    public async Task Status_ReplacedBeforeReplacementPreviewed_ReportsNamedConditionNotBareNotFound()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"));
        var svc = BuildService();
        var originalDigest = (await svc.PreviewAsync(file)).Digest!;
        var sourcePath = await GetJournaledSourcePathAsync(originalDigest);

        // A sibling session allocates the same sequence and writes a different document.
        File.WriteAllText(file, BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed"));

        var status = await svc.StatusAsync(file);

        status.ShouldNotBeNull();
        status!.Found.ShouldBeFalse();
        var replacement = status.Replacement.ShouldNotBeNull();
        replacement.SourcePath.ShouldBe(sourcePath);
        replacement.CurrentDigestJournaled.ShouldBeFalse();
        replacement.SupersededDigests.ShouldBe([originalDigest]);
        replacement.CurrentDigest.ShouldNotBe(originalDigest);

    }

    /// <summary>
    /// The silent shape: the replacing document has its own journal, so status answers
    /// truthfully about the bytes and falsely about the file's history. A caller reading only
    /// <c>found</c>/<c>state</c> is handed a transaction this file never produced.
    /// The replacing journal is imported through the repository directly because
    /// <see cref="PlanLifecycleService.PreviewAsync"/> now refuses to create it (see below);
    /// this reproduces a database written before that guard existed.
    /// </summary>
    [Fact]
    public async Task Status_ReplacedAfterReplacementJournaled_ReportsReplacementBesideTheJournal()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"));
        var svc = BuildService();
        var originalDigest = (await svc.PreviewAsync(file)).Digest!;
        var sourcePath = await GetJournaledSourcePathAsync(originalDigest);

        var siblingSource = BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed");
        File.WriteAllText(file, siblingSource);
        var sibling = new PlanDocumentParser().Parse(siblingSource);
        await _journal.ImportAsync(
            sibling.Plan!, sibling.CanonicalJson!, sibling.Digest!, sourcePath, DateTimeOffset.UnixEpoch.AddDays(1));

        var status = await svc.StatusAsync(file);

        status.ShouldNotBeNull();
        // The journal for the current bytes really does exist — that is precisely why this
        // case is dangerous and must carry the condition rather than read as a clean success.
        status!.Found.ShouldBeTrue();
        status.Digest.ShouldBe(sibling.Digest);
        var replacement = status.Replacement.ShouldNotBeNull();
        replacement.CurrentDigestJournaled.ShouldBeTrue();
        replacement.SupersededDigests.ShouldBe([originalDigest]);
    }

    /// <summary>An ordinary previewed file is not a replacement. Guards the false positive.</summary>
    [Fact]
    public async Task Status_UnchangedAfterPreview_HasNoReplacement()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        await svc.PreviewAsync(file);

        var status = await svc.StatusAsync(file);

        status.ShouldNotBeNull();
        status!.Found.ShouldBeTrue();
        status.Replacement.ShouldBeNull();
    }

    /// <summary>
    /// Re-previewing the same immutable file is explicitly legal (recovery from a stalled
    /// apply re-previews the same bytes and the same digest), so it must not trip the guard.
    /// </summary>
    [Fact]
    public async Task Preview_SameFileTwice_IsNotAReplacement()
    {
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();
        var first = await svc.PreviewAsync(file);

        var second = await svc.PreviewAsync(file);

        second.CanApply.ShouldBeTrue();
        second.Digest.ShouldBe(first.Digest);
        second.Issues.ShouldBeEmpty();
    }

    /// <summary>
    /// Preview refuses a path that already carries another transaction, and — the part that
    /// matters — does not journal the replacing document. Importing it would be the write that
    /// makes the corruption permanent and unreportable.
    /// </summary>
    [Fact]
    public async Task Preview_RefusesReplacedFile_AndDoesNotJournalIt()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"));
        var svc = BuildService();
        var originalDigest = (await svc.PreviewAsync(file)).Digest!;
        var sourcePath = await GetJournaledSourcePathAsync(originalDigest);

        File.WriteAllText(file, BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed"));
        var preview = await svc.PreviewAsync(file);

        preview.CanApply.ShouldBeFalse();
        preview.Issues.ShouldContain(i => i.Code == PlanValidationCodes.SourceReplaced);
        preview.Issues.ShouldContain(i => i.Message.Contains("single-use"));

        // The path still carries exactly one transaction: the original.
        var journaled = await _journal.GetDigestsBySourcePathAsync(sourcePath);
        journaled.ShouldBe([originalDigest]);
    }

    /// <summary>
    /// Apply re-checks independently of preview. Digest equality cannot catch this: the
    /// confirmed digest was computed from the very bytes that replaced the original.
    /// </summary>
    [Fact]
    public async Task Apply_RefusesReplacedFile()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 815, expectedRev: 1, state: "Active"));
        var svc = BuildService();
        var originalDigest = (await svc.PreviewAsync(file)).Digest!;
        var sourcePath = await GetJournaledSourcePathAsync(originalDigest);

        var siblingSource = BatchOnlyPlan(workItemId: 831, expectedRev: 7, state: "Closed");
        File.WriteAllText(file, siblingSource);
        var sibling = new PlanDocumentParser().Parse(siblingSource);
        await _journal.ImportAsync(
            sibling.Plan!, sibling.CanonicalJson!, sibling.Digest!, sourcePath, DateTimeOffset.UnixEpoch.AddDays(1));

        var apply = await svc.ApplyAsync(file, sibling.Digest!, Authorize(sibling.Digest!));

        apply.Failed.ShouldBeTrue();
        apply.Error.ShouldNotBeNull();
        apply.Error!.ShouldContain("single-use");
        // Refused before the loop: no operation row ran.
        apply.Operations.ShouldAllBe(o => o.State != PlanOperationState.Verified);
        await _ado.DidNotReceiveWithAnyArgs().PatchAsync(0, null!, 0, default);
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
            new PlanDocumentParser(), _journal, _pending, _fieldDefinitions, _writer, AdoWriterFixture.Codec, _revisionBound,
            _seedPublish.Orchestrator, _workItems, _seedLinks, _stagedRegistry, _publishIdMap,
            _publishIntent, _config, _paths, boundClock, new StubSteering(() => _steeringMode), new FixtureOrigin(_repoRoot),
            _ruleProvider, Substitute.For<IUnitOfWork>());

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

    [Theory]
    [InlineData("{", """{"rev":4}""", false)]
    [InlineData("""{"rev":4}""", "{}", false)]
    [InlineData("{}", "{}", true)]
    [InlineData("[]", "{}", false)]
    [InlineData("[4]", "[4]", true)]
    [InlineData("[4,5]", "[4]", false)]
    [InlineData("""{"rev":4,"diagnostics":{}}""", """{"rev":4}""", true)]
    public void AcknowledgmentMatchingRequiresReadableNonVacuousEvidence(
        string recorded, string acknowledged, bool matches)
    {
        PlanLifecycleService.ContainsAcknowledgment(recorded, acknowledged).ShouldBe(matches);
    }

    [Fact]
    public async Task Apply_LostAppliedCasWithMissingJournal_RetainsAcknowledgmentWithoutInventingRows()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var digest = (await BuildService().PreviewAsync(file)).Digest!;
        var opId = (await _journal.GetAsync(digest))!.Operations[0].OpId;
        (await _journal.TryTransitionOperationAsync(digest, opId,
            PlanOperationState.Planned, PlanOperationState.Confirmed, DateTimeOffset.UtcNow)).ShouldBeTrue();
        await _journal.ConfirmAsync(digest, DateTimeOffset.UtcNow);
        var confirmed = (await _journal.GetAsync(digest))!;
        confirmed.Operations.Single().State.ShouldBe(PlanOperationState.Confirmed);
        var journal = Substitute.For<IPlanJournalRepository>();
        var acknowledged = false;
        var casLost = false;
        journal.GetAsync(digest, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<PlanJournal?>(acknowledged ? null : confirmed));
        journal.GetDigestsBySourcePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([digest]));
        journal.TryTransitionOperationAsync(digest, opId, PlanOperationState.Confirmed,
            PlanOperationState.Applying, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
        journal.TryRecordAppliedAsync(digest, opId, Arg.Any<string?>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                acknowledged.ShouldBeTrue("the missing-journal race must occur after the transport acknowledgment");
                call.ArgAt<string>(2).ShouldBe("""{"rev":4}""");
                casLost = true;
                return false;
            });
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                acknowledged = true;
                return Task.FromResult(4);
            });

        var result = await BuildService(journal: journal).ApplyAsync(file, digest, Authorize(digest));

        casLost.ShouldBeTrue();
        result.Digest.ShouldBe(digest);
        result.Failed.ShouldBeTrue();
        result.Error.ShouldNotBeNull().ShouldStartWith("acknowledged-effect-unsettled:");
        result.Error.ShouldContain("journal is unavailable");
        result.Error.ShouldContain("Do not retry");
        result.Operations.ShouldBeEmpty();
        result.UnsettledAcknowledgments.Keys.ShouldBe(new[] { opId });
        result.UnsettledAcknowledgments[opId].ShouldBe("""{"rev":4}""");
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3,
            Arg.Any<CancellationToken>());
        await _ado.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await journal.DidNotReceive().CompleteAsync(Arg.Any<string>(), Arg.Any<PlanOperationState>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var retained = (await _journal.GetAsync(digest))!;
        retained.State.ShouldBe(confirmed.State);
        retained.CompletedAt.ShouldBeNull();
        retained.Operations.Single().ShouldBe(confirmed.Operations.Single());
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
        RevisionOf(row.ResultJson).ShouldBe(4);
        DiagnosticsCodeOf(row.ResultJson).ShouldBe("verified");
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
        RevisionOf(row.ResultJson).ShouldBe(3);
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
        RootOf(row.ResultJson).GetProperty("deleted").GetBoolean().ShouldBeTrue();
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
        var seedRoot = RootOf(row.ResultJson);
        seedRoot.GetProperty("identity").GetString().ShouldBe(identity.ToString());
        seedRoot.GetProperty("publishedId").GetInt32().ShouldBe(4242);
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
        RevisionOf(row.ResultJson).ShouldBe(4);
        DiagnosticsCodeOf(row.ResultJson).ShouldBe("verified");
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
        _revisionBound.ClearReceivedCalls();

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

        _revisionBound.FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>()).Returns(
            AuthoritativeSnapshot(42, 3, "Frobnicator", "Doing"));
        _revisionBound.FetchAtRevisionAsync(42, 4, Arg.Any<CancellationToken>()).Returns<WorkItemSnapshot>(_ =>
            throw new InvalidOperationException(
                "Preview must project the add-link operation instead of fetching the future revision."));
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

        var preview = await svc.PreviewAsync(file);
        preview.CanApply.ShouldBeTrue();
        preview.ReviewModel.ShouldNotBeNull().PolicyDecisions.ShouldBeEmpty();
        await _revisionBound.Received(1).FetchAtRevisionAsync(42, 3, Arg.Any<CancellationToken>());
        await _revisionBound.DidNotReceive().FetchAtRevisionAsync(42, 4, Arg.Any<CancellationToken>());
        await _revisionBound.DidNotReceive().FetchAtRevisionAsync(42, 5, Arg.Any<CancellationToken>());
        var digest = preview.Digest!;
        _revisionBound.ClearReceivedCalls();

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
        _revisionBound.ClearReceivedCalls();

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

    // ── AB#881: concise, trustworthy proposal diagnostics on the payload ──
    //
    // These tests inspect ResultJson semantically via JsonDocument (never raw-string
    // equality) so the executor formatter can evolve without a suite-wide rewrite. The
    // load-bearing contract: winning writes carry BOTH `rev` (PATCH ack) and `revision`
    // (readback) plus a diagnostics envelope; every terminal outcome — verified, indeterminate,
    // failed — names WHY, so a CLI/MCP consumer never has to scrape Error strings for keywords.

    [Fact]
    public async Task Apply_WinningBatch_ResultJsonCarriesAckAndReadbackRevision_WithDiagnostics()
    {
        // The winning-execute path stamps the executor's own result JSON (the PATCH ack).
        // The lifecycle then reads back and promotes Applied → Verified, and the atomic
        // record threads the readback's shape into the same row. Both revisions land on
        // the row: `rev` proves what ADO acknowledged, `revision` proves what the refreshed
        // read observed. Divergence would matter, so both are captured — not just one.
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
        // AB#881: both revisions AND diagnostics visible on the atomic Applied+result row.
        RootOf(row.ResultJson).GetProperty("rev").GetInt32().ShouldBe(4);
        RevisionOf(row.ResultJson).ShouldBe(4);
        DiagnosticsCodeOf(row.ResultJson).ShouldBe("verified");
        var fields = DiagnosticFieldsOf(row.ResultJson);
        fields.ShouldHaveSingleItem();
        fields[0].Classification.ShouldBe("exact");
    }

    [Fact]
    public async Task Apply_MultiOp_VerifiedPrefix_IndeterminateMiddle_UntouchedTail_StatusExposesEveryDiagnostic()
    {
        // op-1 verifies. op-2's PATCH lands but the refreshed read shows the server rev
        // did not advance past op-2's expected rev — the "revision-not-advanced" code proves
        // the write is not evidenced; op-2 stays Indeterminate, op-3 is NEVER attempted.
        // StatusAsync must expose the diagnostics on every row a caller cares about, so a
        // human diagnosing a frozen tail sees exactly WHICH row froze it and WHY.
        var plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "System.State": "Active" } },
                { "id": "op-2", "kind": "batch", "workItemId": 42, "expectedRevision": 4,
                  "fields": { "System.State": "Doing" } },
                { "id": "op-3", "kind": "batch", "workItemId": 42, "expectedRevision": 5,
                  "fields": { "System.State": "Done" } }
              ]
            }
            """;
        var file = WritePlan(plan);
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>()).Returns(4);
        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 4, Arg.Any<CancellationToken>()).Returns(5);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(
            BuildWorkItem(42, rev: 4, state: "Active"),  // op-1 readback: advanced 3→4 ✓
            BuildWorkItem(42, rev: 4, state: "Doing"));  // op-2 readback: rev NOT past 4

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue(); // header Failed — tail is not all-verified
        apply.Operations.Count.ShouldBe(3);
        apply.Operations[0].State.ShouldBe(PlanOperationState.Verified);
        apply.Operations[1].State.ShouldBe(PlanOperationState.Indeterminate);
        // op-3 was never attempted — no replay was permitted after the middle stalled.
        apply.Operations[2].State.ShouldNotBe(PlanOperationState.Verified);
        apply.Operations[2].State.ShouldNotBe(PlanOperationState.Failed);
        apply.Operations[2].State.ShouldNotBe(PlanOperationState.Applied);
        apply.Operations[2].ResultJson.ShouldBeNull();

        // StatusAsync surfaces the same diagnostics envelope a CLI/MCP consumer will read.
        var status = await svc.StatusAsync(file);
        status.ShouldNotBeNull();
        status!.Operations.Count.ShouldBe(3);

        DiagnosticsCodeOf(status.Operations[0].ResultJson).ShouldBe("verified");
        RevisionOf(status.Operations[0].ResultJson).ShouldBe(4);

        DiagnosticsCodeOf(status.Operations[1].ResultJson).ShouldBe("revision-not-advanced");
        var diag2 = DiagnosticsOf(status.Operations[1].ResultJson);
        diag2.GetProperty("expectedRevision").GetInt32().ShouldBe(4);
        diag2.GetProperty("observedRevision").GetInt32().ShouldBe(4);

        status.Operations[2].ResultJson.ShouldBeNull();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    public async Task Apply_Batch_RevisionConflict_ResultJsonCarriesRevisionConflictDiagnostics(int serverRevision)
    {
        // A conflict is determinately refused even when ADO omits the current revision.
        // Zero is the adapter's unknown sentinel, not an observed server revision.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Active"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoConflictException(serverRevision, "412"));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Failed);
        row.ResultJson.ShouldNotBeNull();
        DiagnosticsCodeOf(row.ResultJson).ShouldBe("revision-conflict");
        var diag = DiagnosticsOf(row.ResultJson);
        diag.GetProperty("expectedRevision").GetInt32().ShouldBe(3);
        if (serverRevision > 0)
            diag.GetProperty("observedRevision").GetInt32().ShouldBe(serverRevision);
        else
            diag.GetProperty("observedRevision").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Apply_Batch_RuleGateRefuses_ResultJsonPersistsMissingRequiredFieldsDiagnostics()
    {
        // AB#673 close-gate refusal is terminal Failed BEFORE the wire attempt. AB#881
        // adds that the row's ResultJson carries diagnostics.code=missing-required-fields
        // AND names EVERY missing field, so a CLI/MCP client can render exactly what a
        // reviewer must supply — without a follow-up round-trip and without parsing Error.
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;

        // Local source cached at rev 3, in a pre-terminal state, with the gate field unset.
        var source = new WorkItem
        {
            Id = 42, Title = "gated", Type = WorkItemType.Parse("Frobnicator").Value,
        };
        source.ChangeState("Doing");
        source.UpdateField("System.State", "Doing");
        source.MarkSynced(3);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(source);

        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [new ProcessRule(
                [new RuleCondition("when", "System.State", "Done")],
                [new RuleAction("makeRequired", "Custom.Gated", null)],
                IsDisabled: false)]));

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var row = apply.Operations[0];
        row.State.ShouldBe(PlanOperationState.Failed);
        row.ResultJson.ShouldNotBeNull();
        DiagnosticsCodeOf(row.ResultJson).ShouldBe("missing-required-fields");
        MissingFieldsOf(row.ResultJson).ShouldContain("Custom.Gated");
        // The PATCH must never have run — the diagnostics are the gate refusal, not a
        // post-wire readback.
        await _ado.DidNotReceive().PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── AB#881 payload-reader helpers (private to this class) ──────────────

    private static JsonElement RootOf(string? json) =>
        JsonDocument.Parse(json ?? throw new InvalidOperationException("ResultJson is null.")).RootElement;

    private static int RevisionOf(string? json) =>
        RootOf(json).GetProperty("revision").GetInt32();

    private static JsonElement DiagnosticsOf(string? json) =>
        RootOf(json).GetProperty("diagnostics");

    private static string DiagnosticsCodeOf(string? json) =>
        DiagnosticsOf(json).GetProperty("code").GetString()!;

    private static IReadOnlyList<(string Field, string Classification)> DiagnosticFieldsOf(string? json)
    {
        var arr = DiagnosticsOf(json).GetProperty("fields");
        var list = new List<(string, string)>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
            list.Add((el.GetProperty("field").GetString()!, el.GetProperty("classification").GetString()!));
        return list;
    }

    private static IReadOnlyList<string> MissingFieldsOf(string? json)
    {
        var arr = DiagnosticsOf(json).GetProperty("missingFields");
        var list = new List<string>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray()) list.Add(el.GetString()!);
        return list;
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private sealed record DiscussionAppendSetup(
        PlanLifecycleService Service,
        string File,
        string Digest,
        string Body,
        long BenchId,
        SqliteBenchRepository Benches,
        StubDiscussionAuthentication Authentication,
        IWorkItemDiscussionWriter Writer,
        IWorkItemDiscussionReader Reader,
        FakeClock? Clock);

    private sealed class StubDiscussionAuthentication(AuthenticationIdentity identity)
        : IAuthenticationProvider, IBoundAuthenticationMetadata
    {
        public AuthenticationIdentity Identity { get; set; } = identity;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Discussion admission must use bound metadata, not acquire a token.");

        public void InvalidateToken()
            => throw new InvalidOperationException("Discussion admission must not mutate authentication state.");

        public Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
            => Task.FromResult(Identity);
    }

    private async Task<DiscussionAppendSetup> CreateDiscussionAppendProposalAsync(bool withClock = false)
    {
        var workItems = new SqliteWorkItemRepository(_store, _mapper);
        await workItems.SaveAsync(_mapper.Map(new WorkItemSnapshot
        {
            Id = 42,
            Revision = 3,
            Title = "discussion target",
            TypeName = "FixtureType",
            State = "Active",
            Fields = new Dictionary<string, string?> { ["System.Title"] = "discussion target" },
        }));

        var benches = new SqliteBenchRepository(_store);
        var bench = await benches.CreateAsync($"discussion-{Guid.NewGuid():N}");
        bench.ShouldNotBeNull();
        await benches.AddSelectorAsync(bench!.Id, BenchSelector.ForItem(42));
        var stored = (await benches.GetByNameAsync(bench.Name)).ShouldNotBeNull();
        var benchContentsDigest = BenchQueryRule.ContentsDigest(stored!.Selectors);

        const string submittedText = "Discussion note";
        var body = DiscussionPostBodyFormatter.Format("plain", submittedText);
        var source = $$"""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [{
                "id": "comment",
                "kind": "append-comment",
                "workItemId": 42,
                "savedBenchId": "{{bench.Id}}",
                "expectedBenchContentsDigest": "{{benchContentsDigest}}",
                "expectedIdentityId": "identity-1",
                "format": "plain",
                "text": {{JsonSerializer.Serialize(submittedText)}},
                "effectiveBody": {{JsonSerializer.Serialize(body)}}
              }]
            }
            """;
        var file = WritePlan(source, $"discussion-{Guid.NewGuid():N}.json");
        var writer = Substitute.For<IWorkItemDiscussionWriter>();
        var reader = Substitute.For<IWorkItemDiscussionReader>();
        var authentication = new StubDiscussionAuthentication(new AuthenticationIdentity(
            "identity-1", "Test Reviewer", "tenant-1", "object-1", "issuer", "authority",
            "credential-1", "reviewer@example.test"));
        var pendingStore = Substitute.For<IPendingChangeStore>();
        pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        var selectors = new DefaultBenchSelectors(Substitute.For<IIterationService>());
        var admission = new DiscussionAppendAdmission(
            new CurrentBenchResolver(benches, selectors),
            new BenchEvaluator(workItems, Substitute.For<IIterationCalendar>(), pendingStore),
            authentication,
            workItems);
        var clock = withClock ? new FakeClock(DateTimeOffset.UtcNow) : null;
        var service = BuildService(
            workItems: workItems,
            discussionReader: reader,
            discussionWriter: writer,
            discussionAdmission: admission,
            timeProvider: clock);
        var preview = await service.PreviewAsync(file);
        preview.CanApply.ShouldBeTrue(string.Join(Environment.NewLine, preview.Issues.Select(issue => issue.Message)));
        preview.Issues.ShouldBeEmpty();
        preview.Digest.ShouldNotBeNull();
        var digest = preview.Digest!;
        var journal = await _journal.GetAsync(digest);
        journal.ShouldNotBeNull();
        journal.Operations.ShouldHaveSingleItem().Kind.ShouldBe(PlanOperationKind.AppendComment);

        return new DiscussionAppendSetup(service, file, digest, body, bench.Id, benches,
            authentication, writer, reader, clock);
    }

    private async Task<string> GetJournaledSourcePathAsync(string digest, IPlanJournalRepository? journal = null)
    {
        var journaled = await (journal ?? _journal).GetAsync(digest);
        journaled.ShouldNotBeNull();
        return journaled!.SourcePath;
    }

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

    private static string ProviderPlanSource()
    {
        var item = new ProviderWorkItemReference
        {
            LocalId = LocalWorkItemId.New(),
            ExternalIdentity = new ExternalWorkItemIdentity("github", "repo:acme/widgets", "issue:7"),
        };
        return PlanDocumentWriter.Write(new PlanDefinition
        {
            Version = 2,
            Workspace = new PlanWorkspace { Organization = "acme", Project = "cache" },
            Operations =
            [
                new ProviderMutationOperation
                {
                    Id = "provider-delete",
                    OperationKind = PlanOperationKind.Delete,
                    Target = new ProviderWorkItemMutationTarget
                    {
                        Item = item,
                        ExpectedProviderRevision = new ProviderRevisionToken("opaque-revision"),
                    },
                },
            ],
        });
    }

    private static string ProviderBatchPlanSource(ExternalWorkItemIdentity identity) =>
        PlanDocumentWriter.Write(new PlanDefinition
        {
            Version = 2,
            Workspace = new PlanWorkspace { Organization = "acme", Project = "cache" },
            Operations =
            [
                new ProviderMutationOperation
                {
                    Id = "provider-label",
                    OperationKind = PlanOperationKind.Batch,
                    Target = new ProviderWorkItemMutationTarget
                    {
                        Item = new ProviderWorkItemReference
                        {
                            LocalId = LocalWorkItemId.New(),
                            ExternalIdentity = identity,
                        },
                        ExpectedProviderRevision = new ProviderRevisionToken("opaque-revision"),
                    },
                    Fields = new Dictionary<string, string?> { ["System.Tags"] = "priority:high" },
                },
            ],
        });

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
    public async Task Preview_AndApply_BlockWhenDeclaredPolicyCannotLoad()
    {
        File.WriteAllText(_paths.PolicyPath, "{ not json");
        var file = WritePlan(ValidPlanSource());
        var svc = BuildService();

        var preview = await svc.PreviewAsync(file);

        preview.CanApply.ShouldBeFalse();
        var decision = preview.ReviewModel.ShouldNotBeNull().PolicyDecisions.ShouldHaveSingleItem();
        decision.Outcome.ShouldBe(Twig.Domain.Services.Policy.PolicyOutcome.Unavailable);
        decision.IsBlocking.ShouldBeTrue();
        decision.RuleId.ShouldBe(Twig.Domain.Services.Policy.DeclaredPolicyRuleKinds.PolicyLoad);
        preview.ReviewModel.Blockers.ShouldContain(b => b.Kind == "policy" && b.Detail.Contains("not valid JSON"));
        preview.ReviewModel.AuthorizationChoices.ShouldNotContain("apply");

        var apply = await svc.ApplyAsync(file, preview.Digest!, authorization: null);

        apply.Error.ShouldNotBeNull();
        apply.Error.ShouldContain("Declared policy refuses this proposal");
        apply.Error.ShouldContain("policy-load");
        apply.Error.ShouldContain("not valid JSON");
        var journal = await _journal.GetAsync(preview.Digest!);
        journal.ShouldNotBeNull();
        journal!.State.ShouldBe(PlanOperationState.Planned);
    }

    [Fact]
    public async Task Preview_ProjectsProcessRequiredFieldRefusalAsPolicyDecision()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        var digest = (await svc.PreviewAsync(file)).Digest!;
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ProcessRule>>(
            [
                new ProcessRule(DoneCondition, [new RuleAction("makeRequired", "Custom.Gated", null)], IsDisabled: false),
            ]));

        var preview = await svc.PreviewAsync(file);

        preview.CanApply.ShouldBeFalse();
        var model = preview.ReviewModel.ShouldNotBeNull();
        var decision = model.PolicyDecisions.ShouldHaveSingleItem();
        decision.RuleId.ShouldBe("process-rule-gate");
        decision.Kind.ShouldBe("process-required-fields");
        decision.Outcome.ShouldBe(Twig.Domain.Services.Policy.PolicyOutcome.Refuse);
        decision.WorkItemId.ShouldBe(42);
        decision.OpId.ShouldBe("op");
        decision.Reason.ShouldContain("Custom.Gated");
        model.Blockers.ShouldContain(b => b.Kind == "policy" && b.Detail.Contains("Custom.Gated"));
        model.AuthorizationChoices.ShouldNotContain("apply");

        var apply = await svc.ApplyAsync(file, digest, Authorize(digest));

        apply.Failed.ShouldBeTrue();
        var operation = apply.Operations.ShouldHaveSingleItem();
        operation.State.ShouldBe(PlanOperationState.Failed);
        operation.Error.ShouldNotBeNull();
        operation.Error.ShouldContain("Custom.Gated");
        await _ado.DidNotReceive().PatchAsync(
            Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
        (await _journal.GetAsync(digest))!.State.ShouldBe(PlanOperationState.Failed);
    }

    [Fact]
    public async Task Preview_ProjectsProcessRuleLoadFailureAsUnavailablePolicyDecision()
    {
        var file = WritePlan(BatchOnlyPlan(workItemId: 42, expectedRev: 3, state: "Done"));
        var svc = BuildService();
        await svc.PreviewAsync(file);
        _workItems.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(GatedSource());
        _ruleProvider.GetRulesAsync("Frobnicator", Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("offline"));

        var preview = await svc.PreviewAsync(file);

        preview.CanApply.ShouldBeFalse();
        var decision = preview.ReviewModel.ShouldNotBeNull().PolicyDecisions.ShouldHaveSingleItem();
        decision.RuleId.ShouldBe("process-rule-gate");
        decision.Outcome.ShouldBe(Twig.Domain.Services.Policy.PolicyOutcome.Unavailable);
        decision.IsBlocking.ShouldBeTrue();
        decision.Reason.ShouldContain("could not be loaded");
        decision.Reason.ShouldContain("offline");
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
