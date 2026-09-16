using System.Net.Http;
using System.Text.Json.Nodes;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
using Twig.TestKit;

namespace Twig.AgentEfficiencyBaseline;

/// <summary>
/// Offline baseline cases for the plan-write pipeline. Every case drives the real
/// <see cref="PlanLifecycleService"/> from Twig.Infrastructure over an in-memory
/// SQLite journal and NSubstitute fakes for the ADO surface. No live network I/O
/// is performed; the recorded fixture is what a live PATCH+readback would return
/// if ADO behaved exactly as stubbed, and every case labels that provenance in
/// its output. The service, executor, HTML comparer, identity comparer and
/// server-generated policy are the production types.
/// </summary>
internal static class WriteCases
{
    private const string EvidenceKind =
        "Twig.Infrastructure.Plan lifecycle over in-memory SqliteCacheStore and NSubstitute fakes; not live ADO.";
    private const string Organization = "acme";
    private const string Project = "cache";

    public static async Task<IReadOnlyList<Observation>> RunAsync()
    {
        var results = new List<Observation>(20);
        results.Add(await HtmlClosingTagWhitespaceDefectAsync().ConfigureAwait(false));
        results.Add(await HtmlAttributeReorderEquivalentAsync().ConfigureAwait(false));
        results.Add(await HtmlTextDifferenceNegativeAsync().ConfigureAwait(false));
        results.Add(await HtmlAttributeValueDifferenceNegativeAsync().ConfigureAwait(false));
        results.Add(await HtmlPreDifferenceNegativeAsync().ConfigureAwait(false));
        results.Add(await HtmlTextareaDifferenceNegativeAsync().ConfigureAwait(false));
        results.Add(await IdentityRewriteEquivalentPositiveAsync().ConfigureAwait(false));
        results.Add(await IdentityDifferentAccountNegativeAsync().ConfigureAwait(false));
        results.Add(await ScalarMismatchNegativeAsync().ConfigureAwait(false));
        results.Add(await FailedClearNegativeAsync().ConfigureAwait(false));
        results.Add(await UnchangedRevisionAllFieldsAlreadySatisfiedAsync().ConfigureAwait(false));
        results.Add(await StaleRevisionConflictNegativeAsync().ConfigureAwait(false));
        results.Add(await VerifiedPrefixIndeterminateSecondUntouchedTailAsync().ConfigureAwait(false));
        return results;
    }

    // ── HTML: closing-tag whitespace normalization the current comparer refuses ─
    //
    // Synthetic entity-free payload, with a space inserted immediately before
    // each closing tag. This tests the recorded AB#848 mechanism, not the
    // historical 919-byte payload and not an unobserved live ADO response.
    private static async Task<Observation> HtmlClosingTagWhitespaceDefectAsync()
    {
        const string expected = "<p>Alpha</p><p>Beta</p>";
        const string actual = "<p>Alpha </p><p>Beta </p>";
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.Description", dataType: "html");
        fixture.StageBatchAdvance(workItemId: 42, expectedRev: 3, newRev: 4,
            readback: BuildWorkItem(42, rev: 4, fields: [("System.Description", actual)]));

        var plan = BatchPlan(workItemId: 42, expectedRev: 3,
            fields: [("System.Description", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);

        var op = capture.Operations[0];
        // Desired behavior: equivalent block-edge whitespace verifies with a
        // warning. The baseline records the actual lifecycle result.
        var desiredSatisfied = op.State == "Verified" && op.Warning is not null;
        // Safety holds so long as the comparer refuses; if a future change ever
        // accepted this whitespace as Verified WITHOUT the normalization warning
        // (i.e. as byte-equal), that would be a silent acceptance and fail.
        var safetyPassed = !(op.State == "Verified" && op.Warning is null);

        return Observation(
            id: "write.html.closing-tag-whitespace.defect",
            classification: "current defect",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan, ("expectedHtml", expected), ("actualHtml", actual),
                ("fieldReference", "System.Description"),
                ("fieldDataType", "html"),
                ("intent", "Reproduce inserted whitespace immediately before block closing tags.")),
            output: capture.ToOutputNode(
                observedDefect: desiredSatisfied ? null : "The inserted closing-tag spaces remain significant to readback.",
                desiredBehaviour: "Verified with a CanonicalizedHtml warning for equivalent block-edge whitespace."));
    }

    // ── HTML: attribute reorder + tag case — the comparer correctly accepts ────
    private static async Task<Observation> HtmlAttributeReorderEquivalentAsync()
    {
        const string expected = "<a href=\"/x\" class=\"c\">L</a>";
        const string actual = "<A CLASS=\"c\" HREF=\"/x\">L</A>";
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.Description", dataType: "html");
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, fields: [("System.Description", actual)]));

        var plan = BatchPlan(42, 3, [("System.Description", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        var desiredSatisfied = op.State == "Verified" && op.Warning is not null;
        var safetyPassed = desiredSatisfied;

        return Observation(
            id: "write.html.attribute-reorder.equivalent-positive",
            classification: "already fixed",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("expectedHtml", expected),
                ("actualHtml", actual),
                ("intent", "Attribute order and tag case reshuffles are canonicalized; must Verify with warning.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Verified with a CanonicalizedHtml warning naming System.Description."));
    }

    // ── HTML material text difference: comparer correctly refuses ─────────────
    private static Task<Observation> HtmlTextDifferenceNegativeAsync() =>
        HtmlMaterialDifferenceCaseAsync(
            id: "write.html.text.material-difference-negative",
            expected: "<p>Verified</p>",
            actual: "<p>Rejected</p>",
            intent: "Textual difference is material; comparer must refuse and settle Indeterminate.");

    private static Task<Observation> HtmlAttributeValueDifferenceNegativeAsync() =>
        HtmlMaterialDifferenceCaseAsync(
            id: "write.html.attribute-value.material-difference-negative",
            expected: "<a href=\"/x\">L</a>",
            actual: "<a href=\"/y\">L</a>",
            intent: "Attribute value difference is material; comparer must refuse and settle Indeterminate.");

    private static Task<Observation> HtmlPreDifferenceNegativeAsync() =>
        HtmlMaterialDifferenceCaseAsync(
            id: "write.html.pre.material-difference-negative",
            expected: "<pre>  keep\tme</pre>",
            actual: "<pre>keep me</pre>",
            intent: "Preformatted whitespace is significant; comparer must refuse and settle Indeterminate.");

    private static Task<Observation> HtmlTextareaDifferenceNegativeAsync() =>
        HtmlMaterialDifferenceCaseAsync(
            id: "write.html.textarea.material-difference-negative",
            expected: "<textarea>alpha\nbeta</textarea>",
            actual: "<textarea>alpha beta</textarea>",
            intent: "Textarea inner text is significant; comparer must refuse and settle Indeterminate.");

    private static async Task<Observation> HtmlMaterialDifferenceCaseAsync(
        string id, string expected, string actual, string intent)
    {
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.Description", dataType: "html");
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, fields: [("System.Description", actual)]));

        var plan = BatchPlan(42, 3, [("System.Description", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        // Safety negative: a material difference must never Verify. Today it does
        // not; if it ever does, safety flips.
        var safetyPassed = op.State != "Verified";
        var desiredSatisfied = op.State == "Indeterminate";

        return Observation(
            id: id,
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("expectedHtml", expected),
                ("actualHtml", actual),
                ("intent", intent)),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Indeterminate: the difference is material and refusal is correct."));
    }

    // ── Identity rewrite: expected @ notation, ADO returns display rendering ──
    //
    // AB#802: the field is declared as identity by ADO, and the stable-key
    // reduction proves the same account landed. Verified with warning.
    private static async Task<Observation> IdentityRewriteEquivalentPositiveAsync()
    {
        const string expected = "baseline@example.test";
        const string actual = "Baseline User (baseline example.test)";
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.AssignedTo", dataType: "string", isIdentity: true);
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, fields: [("System.AssignedTo", actual)]));

        var plan = BatchPlan(42, 3, [("System.AssignedTo", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        var desiredSatisfied = op.State == "Verified" && op.Warning is not null;
        var safetyPassed = desiredSatisfied;

        return Observation(
            id: "write.identity.rewrite.equivalent-positive",
            classification: "already fixed",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("expectedIdentity", expected),
                ("actualIdentity", actual),
                ("fieldReference", "System.AssignedTo"),
                ("fieldIsIdentity", "true"),
                ("intent", "Identity rewrite from staged form to Display Name (unique name) must Verify with warning.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Verified with a CanonicalizedIdentity warning naming System.AssignedTo."));
    }

    // ── Identity: two different accounts must NEVER be treated as one ─────────
    private static async Task<Observation> IdentityDifferentAccountNegativeAsync()
    {
        const string expected = "baseline@example.test";
        const string actual = "Other User (other example.test)";
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.AssignedTo", dataType: "string", isIdentity: true);
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, fields: [("System.AssignedTo", actual)]));

        var plan = BatchPlan(42, 3, [("System.AssignedTo", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        // Safety negative: two different accounts must never be treated as one.
        var safetyPassed = op.State != "Verified";
        var desiredSatisfied = op.State == "Indeterminate";

        return Observation(
            id: "write.identity.different-account.negative",
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("expectedIdentity", expected),
                ("actualIdentity", actual),
                ("intent", "Distinct accounts must never be conflated by the identity comparer.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Indeterminate: the identities denote different accounts."));
    }

    // ── Plain scalar with genuinely different value: strict refusal ───────────
    private static async Task<Observation> ScalarMismatchNegativeAsync()
    {
        const string expected = "Doing";
        const string actual = "To do";
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.State", dataType: "string");
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, state: actual));

        var plan = BatchPlan(42, 3, [("System.State", expected)]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        var safetyPassed = op.State != "Verified";
        var desiredSatisfied = op.State == "Indeterminate";

        return Observation(
            id: "write.scalar.true-mismatch.negative",
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("expectedValue", expected),
                ("actualValue", actual),
                ("intent", "A plain scalar that read back differently must not be normalized away.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Indeterminate: server value contradicts the plan."));
    }

    // ── Clear requested; server still reports a non-empty value ───────────────
    private static async Task<Observation> FailedClearNegativeAsync()
    {
        using var fixture = new LifecycleFixture();
        fixture.StubField("Custom.Note", dataType: "string");
        fixture.StageBatchAdvance(42, 3, 4,
            BuildWorkItem(42, rev: 4, fields: [("Custom.Note", "still-here")]));

        // BatchPlan cannot represent explicit-null (its helper stringifies values),
        // so hand-craft the plan JSON here to carry the JSON null.
        const string plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-clear", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "Custom.Note": null } }
              ]
            }
            """;
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        var safetyPassed = op.State != "Verified";
        var desiredSatisfied = op.State == "Indeterminate";

        return Observation(
            id: "write.clear.did-not-take.negative",
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("intent", "A clear that did not take must never be reported as landed.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Indeterminate: field still reflects a non-empty value after the clear."));
    }

    // ── Every field already satisfied; PatchAsync stubbed to advance revision ─
    //
    // Per parent guidance: current strict contract refuses a no-op advance as
    // Indeterminate. That refusal is the correct answer under the contract — no
    // observed advance means unproven mutation. DesiredSatisfied is TRUE for
    // conservative refusal; the observable limitation is recorded plainly.
    private static async Task<Observation> UnchangedRevisionAllFieldsAlreadySatisfiedAsync()
    {
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.State", dataType: "string");
        // Stub: PATCH returns the same revision the plan expected (no advance).
        // The lifecycle transitions to Applying, the executor "applies" (PATCH
        // succeeded on the wire) and then readback observes the item still at
        // the pre-op revision — Indeterminate.
        fixture.Ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(3);
        fixture.Ado.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildWorkItem(42, rev: 3, state: "Doing"));

        var plan = BatchPlan(42, 3, [("System.State", "Doing")]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        // The strict contract's answer for an unadvanced revision is refusal.
        var desiredSatisfied = op.State == "Indeterminate";
        var safetyPassed = op.State != "Verified";

        return Observation(
            id: "write.unchanged-revision.conservative-refusal",
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("intent",
                 "Every field already carries the desired value; server returns the unchanged revision. Under the strict contract, unadvanced revision is unproven mutation.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Indeterminate is the correct refusal; observable limitation is that a genuine no-op is indistinguishable from a lost write."));
    }

    // ── 412 revision conflict → deterministic Failure ─────────────────────────
    private static async Task<Observation> StaleRevisionConflictNegativeAsync()
    {
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.State", dataType: "string");
        fixture.Ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new AdoConflictException(serverRev: 5));

        var plan = BatchPlan(42, 3, [("System.State", "Doing")]);
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op = capture.Operations[0];
        var desiredSatisfied = op.State == "Failed"
            && op.Error is not null
            && op.Error.Contains("server rev=5", StringComparison.Ordinal);
        // Safety: 412 with a strictly newer server revision must NOT settle
        // Verified; a Verified here would be a silent overwrite of newer state.
        var safetyPassed = op.State != "Verified";

        return Observation(
            id: "write.stale-revision.412.negative",
            classification: "expected refusal",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("intent", "412 must be deterministic Failure carrying the server revision.")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "Failed with 'Revision conflict: server rev=5.'"));
    }

    // ── Whole-lifecycle: op-1 Verified, op-2 Indeterminate, op-3 untouched ───
    //
    // Two work items are used so op-2 (the failing one) does not depend on the
    // authoritative snapshot the process-rule gate would otherwise cache.
    private static async Task<Observation> VerifiedPrefixIndeterminateSecondUntouchedTailAsync()
    {
        using var fixture = new LifecycleFixture();
        fixture.StubField("System.State", dataType: "string");

        // op-1: PATCH advances 3→4; readback at rev 4 proves the state.
        fixture.Ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);
        var rev4 = BuildWorkItem(42, rev: 4, state: "Doing");
        fixture.Ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(rev4);

        // op-2: PATCH throws a transient network failure. Executor catches
        // Exception and returns Indeterminate. The lifecycle attempts a readback
        // while still Applying to try to reconcile — that FetchAsync ALSO
        // throws, so the readback stays Indeterminate and the op terminalises
        // Indeterminate. AB#721 carry-forward means op-2 will not refetch the
        // authoritative snapshot at rev 7, so a rule-provider is unnecessary.
        fixture.Ado.PatchAsync(99, Arg.Any<IReadOnlyList<FieldChange>>(), 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network drop mid-response"));
        fixture.Ado.FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("readback network drop"));

        // op-3: no stubs at all; the first non-Verified terminal must halt the
        // tail before any call reaches this item.
        const string plan = """
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-a", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "System.State": "Doing" } },
                { "id": "op-b", "kind": "batch", "workItemId": 99, "expectedRevision": 7,
                  "fields": { "System.State": "Doing" } },
                { "id": "op-c", "kind": "batch", "workItemId": 55, "expectedRevision": 2,
                  "fields": { "System.State": "Doing" } }
              ]
            }
            """;
        var capture = await fixture.RunAsync(plan).ConfigureAwait(false);
        var op1 = capture.Operations[0];
        var op2 = capture.Operations[1];
        var op3 = capture.Operations[2];
        var desiredSatisfied =
            op1.State == "Verified"
            && op2.State == "Indeterminate"
            && op3.State == "Confirmed"
            && capture.FakeCalls.PatchInvocationsForItem(55) == 0
            && capture.FakeCalls.FetchInvocationsForItem(55) == 0;
        // Safety: op-3 must never be attempted after an Indeterminate second op.
        var safetyPassed = desiredSatisfied
            && capture.FakeCalls.PatchInvocationsForItem(42) == 1
            && capture.FakeCalls.PatchInvocationsForItem(99) == 1;

        return Observation(
            id: "write.lifecycle.verified-prefix.indeterminate-second.untouched-tail",
            classification: "already fixed",
            desiredSatisfied: desiredSatisfied,
            safetyPassed: safetyPassed,
            input: BuildInput(plan,
                ("intent",
                 "Whole-lifecycle three-op plan: op-1 Verified, op-2 Indeterminate, op-3 untouched (still Confirmed).")),
            output: capture.ToOutputNode(
                observedDefect: null,
                desiredBehaviour: "op-1 Verified, op-2 Indeterminate, op-3 remains Confirmed and no ADO calls target #55."));
    }


    // ──────────────────────────────────────────────────────────────────────────
    // Fixture and helpers
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class LifecycleFixture : IDisposable
    {
        public string RepoRoot { get; }
        public SqliteCacheStore Store { get; }
        public SqlitePlanJournalRepository Journal { get; }
        public IPendingChangeReader Pending { get; } = Substitute.For<IPendingChangeReader>();
        public IFieldDefinitionStore FieldDefinitions { get; } = Substitute.For<IFieldDefinitionStore>();
        public IAdoWorkItemService Ado { get; } = Substitute.For<IAdoWorkItemService>();
        public IRevisionBoundAdoWorkItemService RevisionBound { get; } = Substitute.For<IRevisionBoundAdoWorkItemService>();
        public IWorkItemRepository WorkItems { get; } = Substitute.For<IWorkItemRepository>();
        public ISeedLinkRepository SeedLinks { get; } = Substitute.For<ISeedLinkRepository>();
        public IStagedIdentityRegistry StagedRegistry { get; } = Substitute.For<IStagedIdentityRegistry>();
        public IPublishIdMapRepository PublishIdMap { get; } = Substitute.For<IPublishIdMapRepository>();
        public IPublishIntentRepository PublishIntent { get; } = Substitute.For<IPublishIntentRepository>();
        public SeedPublishOrchestrator SeedPublish { get; }
        public PlanLifecycleService Service { get; }
        public TwigConfiguration Config { get; }
        public TwigPaths Paths { get; }
        private readonly WorkItemMapper _mapper = new();

        public LifecycleFixture()
        {
            RepoRoot = Path.Combine(Path.GetTempPath(), $"twig-baseline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RepoRoot);
            var twigDir = Path.Combine(RepoRoot, ".twig");
            Directory.CreateDirectory(twigDir);
            Store = new SqliteCacheStore("Data Source=:memory:");
            Journal = new SqlitePlanJournalRepository(Store);

            Config = new TwigConfiguration { Organization = Organization, Project = Project };
            Paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"),
                Path.Combine(twigDir, "twig.db"), RepoRoot);

            Pending.GetAllChangesAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PendingChangeDetail>>(Array.Empty<PendingChangeDetail>()));
            PublishIdMap.GetAllMappingsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PublishMapping>>(Array.Empty<PublishMapping>()));
            RevisionBound.FetchAtRevisionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var id = ci.ArgAt<int>(0);
                    var rev = ci.ArgAt<int>(1);
                    var ct = ci.ArgAt<CancellationToken>(2);
                    var source = await WorkItems.GetByIdAsync(id, ct).ConfigureAwait(false);
                    return source is null
                        ? new WorkItemSnapshot
                        {
                            Id = id,
                            Revision = rev,
                            TypeName = string.Empty,
                            Title = string.Empty,
                            State = string.Empty,
                            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                        }
                        : _mapper.ToSnapshot(source);
                });

            // Real orchestrator over dummy substitutes; never invoked in these
            // fixtures because none of the plans stage a publish-seed operation.
            SeedPublish = new SeedPublishOrchestrator(
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
                ReferenceProfileBuilder.SprintPolicy());

            Service = new PlanLifecycleService(
                new PlanDocumentParser(),
                Journal, Pending, FieldDefinitions, Ado, RevisionBound, SeedPublish,
                WorkItems, SeedLinks, StagedRegistry, PublishIdMap, PublishIntent,
                Config, Paths, TimeProvider.System, new HumanSteering());
        }

        public void StubField(string referenceName, string dataType, bool isIdentity = false)
        {
            var definition = new FieldDefinition(referenceName, referenceName, dataType, IsReadOnly: false)
            {
                IsIdentity = isIdentity,
            };
            FieldDefinitions.GetByReferenceNameAsync(referenceName, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<FieldDefinition?>(definition));
        }

        public void StageBatchAdvance(int workItemId, int expectedRev, int newRev, WorkItem readback)
        {
            Ado.PatchAsync(workItemId, Arg.Any<IReadOnlyList<FieldChange>>(), expectedRev, Arg.Any<CancellationToken>())
                .Returns(newRev);
            Ado.FetchAsync(workItemId, Arg.Any<CancellationToken>()).Returns(readback);
        }

        public async Task<Capture> RunAsync(string planJson)
        {
            var file = Path.Combine(RepoRoot, "plan.json");
            await File.WriteAllTextAsync(file, planJson).ConfigureAwait(false);
            var preview = await Service.PreviewAsync(file).ConfigureAwait(false);
            var digest = preview.Digest
                ?? throw new InvalidOperationException("Baseline fixture: preview did not produce a digest.");
            var apply = await Service.ApplyAsync(file, digest,
                new ProposalAuthorization
                {
                    Digest = digest,
                    Mode = ProposalAuthorizationGate.RequiredMode(SessionSteeringMode.HumanSteered),
                    AuthorizerIdentity = "Baseline Authorizer",
                    AuthorizedAt = DateTimeOffset.UnixEpoch,
                }).ConfigureAwait(false);

            var journal = await Journal.GetAsync(digest).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Baseline fixture: journal row missing after apply.");
            var operations = journal.Operations
                .OrderBy(o => o.Ordinal)
                .Select(o => new OperationSnapshot(
                    Ordinal: o.Ordinal,
                    OpId: o.OpId,
                    Kind: o.Kind.ToString(),
                    State: o.State.ToString(),
                    ResultJson: o.ResultJson,
                    Error: o.Error,
                    Warning: o.Warning,
                    Applied: o.AppliedAt.HasValue,
                    Verified: o.VerifiedAt.HasValue))
                .ToArray();

            var patchCounts = new Dictionary<int, int>();
            var fetchCounts = new Dictionary<int, int>();
            foreach (var call in Ado.ReceivedCalls())
            {
                var method = call.GetMethodInfo().Name;
                var args = call.GetArguments();
                if (args.Length == 0 || args[0] is not int id)
                    continue;
                if (method == nameof(IAdoWorkItemService.PatchAsync))
                    patchCounts[id] = patchCounts.TryGetValue(id, out var pv) ? pv + 1 : 1;
                else if (method == nameof(IAdoWorkItemService.FetchAsync))
                    fetchCounts[id] = fetchCounts.TryGetValue(id, out var fv) ? fv + 1 : 1;
            }
            var calls = new FakeCallCounts(patchCounts, fetchCounts);

            return new Capture(digest, apply.Failed, apply.Error, operations, calls);
        }

        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(RepoRoot, recursive: true);
        }
    }

    private sealed class HumanSteering : ISessionSteeringModeProvider
    {
        public SessionSteeringMode Resolve() => SessionSteeringMode.HumanSteered;
    }

    private sealed record OperationSnapshot(
        int Ordinal, string OpId, string Kind, string State,
        string? ResultJson, string? Error, string? Warning,
        bool Applied, bool Verified);

    private sealed record FakeCallCounts(
        IReadOnlyDictionary<int, int> PatchByItem,
        IReadOnlyDictionary<int, int> FetchByItem)
    {
        public int PatchInvocationsForItem(int id) => PatchByItem.TryGetValue(id, out var v) ? v : 0;
        public int FetchInvocationsForItem(int id) => FetchByItem.TryGetValue(id, out var v) ? v : 0;
        public int TotalPatchInvocations => PatchByItem.Values.Sum();
        public int TotalFetchInvocations => FetchByItem.Values.Sum();
    }

    private sealed record Capture(
        string Digest, bool Failed, string? Error,
        IReadOnlyList<OperationSnapshot> Operations, FakeCallCounts FakeCalls)
    {
        public JsonNode ToOutputNode(string? observedDefect, string desiredBehaviour)
        {
            var ops = new JsonArray();
            foreach (var op in Operations)
            {
                ops.Add(new JsonObject
                {
                    ["ordinal"] = op.Ordinal,
                    ["opId"] = op.OpId,
                    ["kind"] = op.Kind,
                    ["state"] = op.State,
                    ["resultJson"] = op.ResultJson,
                    ["error"] = op.Error,
                    ["warning"] = op.Warning,
                    ["appliedStamped"] = op.Applied,
                    ["verifiedStamped"] = op.Verified,
                });
            }

            var patchByItem = new JsonObject();
            foreach (var kv in FakeCalls.PatchByItem)
                patchByItem[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = kv.Value;
            var fetchByItem = new JsonObject();
            foreach (var kv in FakeCalls.FetchByItem)
                fetchByItem[kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = kv.Value;

            var result = new JsonObject
            {
                ["source"] = EvidenceKind,
                ["digest"] = Digest,
                ["applyFailed"] = Failed,
                ["applyError"] = Error,
                ["operations"] = ops,
                ["fakeCalls"] = new JsonObject
                {
                    ["patchByItem"] = patchByItem,
                    ["fetchByItem"] = fetchByItem,
                    ["totalPatch"] = FakeCalls.TotalPatchInvocations,
                    ["totalFetch"] = FakeCalls.TotalFetchInvocations,
                },
                ["desiredBehaviour"] = desiredBehaviour,
            };
            if (observedDefect is not null)
                result["observedDefect"] = observedDefect;
            return result;
        }
    }

    private static Observation Observation(
        string id,
        string classification,
        bool desiredSatisfied,
        bool safetyPassed,
        JsonNode input,
        JsonNode output)
        => new(id, desiredSatisfied ? (classification == "current defect" ? "already fixed" : classification) : "current defect",
            desiredSatisfied, input.ToJsonString(), output.ToJsonString(), safetyPassed);

    private static JsonNode BuildInput(string planJson, params (string Key, string Value)[] context)
    {
        var input = new JsonObject
        {
            ["source"] = EvidenceKind,
            ["planJson"] = planJson,
        };
        foreach (var (key, value) in context)
            input[key] = value;
        return input;
    }

    private static string BatchPlan(int workItemId, int expectedRev,
        IReadOnlyList<(string Name, string Value)> fields)
    {
        var body = string.Join(", ", fields.Select(f =>
            $"\"{f.Name}\": {JsonData.Serialize(f.Value)}"));
        return $$"""
            {
              "version": 1,
              "workspace": { "organization": "acme", "project": "cache" },
              "operations": [
                { "id": "op-1", "kind": "batch", "workItemId": {{workItemId}}, "expectedRevision": {{expectedRev}},
                  "fields": { {{body}} } }
              ]
            }
            """;
    }

    private static WorkItem BuildWorkItem(int id, int rev, string? state = null,
        IReadOnlyList<(string Name, string? Value)>? fields = null)
    {
        var item = new WorkItem { Id = id, Title = $"item-{id}" };
        item.MarkSynced(rev);
        if (state is not null)
        {
            item.ChangeState(state);
            item.UpdateField("System.State", state);
        }
        if (fields is not null)
            foreach (var (name, value) in fields)
                item.UpdateField(name, value);
        return item;
    }
}
