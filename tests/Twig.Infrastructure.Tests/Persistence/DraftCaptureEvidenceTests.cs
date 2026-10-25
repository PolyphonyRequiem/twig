using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Tests.Ado;
using NSubstitute;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class DraftCaptureEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"twig-capture-evidence-{Guid.NewGuid():N}");
    private QualifiedWorkItemKey Key => new(_directory, "connection", "binding", 42);
    private PlanOrigin Origin => new(_directory, "fingerprint", 1, "connection", "binding", 1,
        "selection", 1, "identity", "method", "credential", "tenant", "object", "issuer", "authority", null);
    private static PlanWorkspace Workspace => new() { Organization = "scope", Project = "project" };
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "twig.db")};Pooling=False";
    public DraftCaptureEvidenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task QualifiedFenceRechecksNewJournalAuditInsideTheOwningTransactionAndRollsBack()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache);
        var attempted = false;
        cache.GetConnection().CreateFunction("qualified_fence_probe", () =>
        {
            attempted = true;
            cache.ActiveTransaction.ShouldNotBeNull();
            using var update = cache.GetConnection().CreateCommand();
            update.Transaction = cache.ActiveTransaction;
            update.CommandText = "UPDATE pending.proposal_journals SET authorizer_identity='concurrent-audit';";
            update.ExecuteNonQuery().ShouldBe(1);
            return 0;
        });
        Execute(cache, """
            CREATE TEMP TRIGGER qualified_fence_probe AFTER INSERT ON pending.proposal_journals
            BEGIN SELECT qualified_fence_probe(); END;
            """);
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            new SqliteDraftProposalCaptureStore(cache).CaptureQualifiedAsync(draft, snapshot, Origin, Workspace,
                Path.Combine(_directory, "proposals"), SqliteDraftCaptureEvidenceStore.Fingerprint(evidence)));
        refused.Message.ShouldStartWith("capture-not-reviewable:");
        attempted.ShouldBeTrue();
        cache.ActiveTransaction.ShouldBeNull();
        Count(cache, "proposal_journals").ShouldBe(0);
        Count(cache, "proposal_operations").ShouldBe(0);
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Changes.ShouldBe(draft.Changes);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("confirmed")]
    [InlineData("completed")]
    [InlineData("mode")]
    [InlineData("unknown-mode")]
    [InlineData("empty-authorizer")]
    [InlineData("empty-mode")]
    [InlineData("receipt")]
    [InlineData("authorizer")]
    [InlineData("rationale")]
    [InlineData("review")]
    [InlineData("authorized")]
    [InlineData("operation-state")]
    [InlineData("operation-started")]
    [InlineData("operation-applied")]
    [InlineData("operation-verified")]
    public async Task QualifiedRetentionRefusesAdvancedOrAuthorizationBearingReuseWithoutChangingLegacyCapture(string fault)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache);
        var store = new SqliteDraftProposalCaptureStore(cache);
        var fingerprint = SqliteDraftCaptureEvidenceStore.Fingerprint(evidence);
        var directory = Path.Combine(_directory, "proposals");
        var first = await store.CaptureQualifiedAsync(draft, snapshot, Origin, Workspace, directory, fingerprint);
        cache.ActiveTransaction.ShouldBeNull();
        var replay = await store.CaptureQualifiedAsync(draft, snapshot, Origin, Workspace, directory, fingerprint);
        replay.Digest.ShouldBe(first.Digest);
        replay.PreviewedAt.ShouldBe(first.PreviewedAt);
        replay.LastPreviewedAt.ShouldBe(first.LastPreviewedAt);
        if (fault == "receipt")
        {
            var operation = first.Operations.Single();
            var journals = new SqlitePlanJournalRepository(cache);
            var receipt = new PlanOutcomeReceipt
            {
                ReceiptId = Guid.NewGuid().ToString("N"), Digest = first.Digest, OpId = operation.OpId,
                Kind = PlanOutcomeKind.Retired, RequestJson = operation.RequestJson,
                Origin = Origin, AuthorizingOrigin = Origin,
                Authorization = new ProposalAuthorization
                {
                    Digest = first.Digest, Mode = ProposalAuthorizationMode.Human,
                    AuthorizerIdentity = "fixture-only", Rationale = "Retired without admission in local test.",
                    AuthorizedAt = DateTimeOffset.UtcNow,
                },
                EvidenceJson = "{\"neverAdmitted\":true,\"mutationIssued\":false}",
            };
            (await journals.TryAppendReceiptAsync(receipt, PlanOperationState.Planned, expectedResultJson: null))
                .ShouldBeTrue();
            var settled = (await journals.GetAsync(first.Digest)).ShouldNotBeNull();
            settled.State.ShouldBe(PlanOperationState.Planned);
            settled.AuthorizationMode.ShouldBeNull();
            settled.AuthorizerIdentity.ShouldBeNull();
            settled.AuthorizedAt.ShouldBeNull();
            settled.ReviewModelJson.ShouldBeNull();
            settled.Rationale.ShouldBeNull();
            settled.ConfirmedAt.ShouldBeNull();
            settled.CompletedAt.ShouldBeNull();
            var unchangedOperation = settled.Operations.Single();
            unchangedOperation.State.ShouldBe(PlanOperationState.Planned);
            unchangedOperation.StartedAt.ShouldBeNull();
            unchangedOperation.AppliedAt.ShouldBeNull();
            unchangedOperation.VerifiedAt.ShouldBeNull();
            unchangedOperation.OutcomeReceipt.ShouldNotBeNull().ReceiptId.ShouldBe(receipt.ReceiptId);
        }
        else Execute(cache, fault switch
        {
            "state" => "UPDATE pending.proposal_journals SET state='Confirmed';",
            "confirmed" => "UPDATE pending.proposal_journals SET confirmed_at='2026-01-01T00:00:00Z';",
            "completed" => "UPDATE pending.proposal_journals SET completed_at='2026-01-01T00:00:00Z';",
            "mode" => "UPDATE pending.proposal_journals SET authorization_mode='human';",
            "unknown-mode" => "UPDATE pending.proposal_journals SET authorization_mode='unrecognized';",
            "empty-mode" => "UPDATE pending.proposal_journals SET authorization_mode='';",
            "authorizer" => "UPDATE pending.proposal_journals SET authorizer_identity='untrusted-string';",
            "empty-authorizer" => "UPDATE pending.proposal_journals SET authorizer_identity='';",
            "rationale" => "UPDATE pending.proposal_journals SET rationale='prior-rationale';",
            "review" => "UPDATE pending.proposal_journals SET review_model_json='{}';",
            "authorized" => "UPDATE pending.proposal_journals SET authorized_at='2026-01-01T00:00:00Z';",
            "operation-state" => "UPDATE pending.proposal_operations SET state='Confirmed';",
            "operation-started" => "UPDATE pending.proposal_operations SET started_at='2026-01-01T00:00:00Z';",
            "operation-applied" => "UPDATE pending.proposal_operations SET applied_at='2026-01-01T00:00:00Z';",
            "operation-verified" => "UPDATE pending.proposal_operations SET verified_at='2026-01-01T00:00:00Z';",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        });
        var before = ReadRows();
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.CaptureQualifiedAsync(draft, snapshot, Origin, Workspace, directory, fingerprint));
        refused.Message.ShouldStartWith("capture-not-reviewable:");
        cache.ActiveTransaction.ShouldBeNull();
        ReadRows().ShouldBe(before);
        // Legacy export/recovery retains its prior exact-binding reuse semantics.
        var legacy = await store.CaptureObservedAsync(draft, snapshot, Origin, Workspace, directory, fingerprint);
        legacy.Digest.ShouldBe(first.Digest);
        ReadRows().ShouldBe(before);
        Count(cache, "proposal_journals").ShouldBe(1);
        Count(cache, "proposal_operations").ShouldBe(1);
        Count(cache, "proposal_receipts").ShouldBe(fault == "receipt" ? 1 : 0);
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Changes.ShouldBe(draft.Changes);
        File.Exists(first.SourcePath).ShouldBeFalse();

        string[] ReadRows()
        {
            using var command = cache.GetConnection().CreateCommand();
            command.CommandText = """
                SELECT * FROM pending.proposal_journals;
                SELECT * FROM pending.proposal_operations;
                SELECT * FROM pending.proposal_receipts;
                SELECT * FROM pending.recoverable_work_item_drafts;
                SELECT * FROM metadata ORDER BY key;
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            do
            {
                while (reader.Read())
                    rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
                        reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
            } while (reader.NextResult());
            return rows.ToArray();
        }
    }

    [Fact]
    public async Task ServedObservationRoundTripsRulesAndCapturesExactNullAndEmptyValues()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache, "System.AssignedTo", null, "");
        evidence = evidence with { Rules = [new([], [], false, RuleCustomization.From("custom"), "retained")] };
        await new SqliteDraftCaptureEvidenceStore(cache).RecordAsync(evidence, snapshot, default);
        var reloaded = new SqliteDraftCaptureEvidenceStore(cache).Read(Key);
        reloaded.Rules!.Single().CustomizationOrUnknown.Token.ShouldBe("custom");
        var journal = await CaptureAsync(cache, draft, snapshot, reloaded);
        journal.DraftCapture!.Version.ShouldBe(2);
        journal.DraftCapture.EvidenceFingerprint.ShouldBe(SqliteDraftCaptureEvidenceStore.Fingerprint(reloaded));
        journal.AuthorizationMode.ShouldBeNull();
        journal.ConfirmedAt.ShouldBeNull();
        journal.State.ShouldBe(PlanOperationState.Planned);
        var retained = (await new SqlitePlanJournalRepository(cache).GetAsync(journal.Digest))!;
        retained.Error.ShouldNotBeNull().ShouldContain("export-incomplete");
        var batch = (BatchOperation)new PlanDocumentParser().Parse(journal.CanonicalJson).Plan!.Operations.Single();
        batch.Fields["System.AssignedTo"].ShouldBe("");
        retained.DraftCapture!.Draft.Changes.Single().BaselineValue.ShouldBeNull();
    }

    [Theory]
    [InlineData("item")]
    [InlineData("subtree")]
    [InlineData("query-team")]
    [InlineData("query-display")]
    [InlineData("query-canonical")]
    [InlineData("query-configured")]
    [InlineData("mixed")]
    public async Task NativeReceiptRoundTripsSelectorDiscriminatorsAndRetainsExactCaptureFence(string shape)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache, recordEvidence: false);
        var configured = new BenchQueryRule(null, null, [], []).ToSelector();
        BenchSelector[] selectors = shape switch
        {
            "item" => [BenchSelector.ForItem(42)],
            "subtree" => [BenchSelector.ForSubtree(42)],
            "query-team" => [BenchSelector.ForItem(42), BenchSelector.ForCurrentSprint(null)],
            "query-display" => [BenchSelector.ForItem(42), BenchSelector.ForCurrentSprint("fixture display")],
            "query-canonical" => [BenchSelector.ForItem(42), BenchSelector.ForCurrentSprintCanonical("fixture-account")],
            "query-configured" => [BenchSelector.ForItem(42), configured],
            "mixed" => [BenchSelector.ForItem(42), BenchSelector.ForSubtree(42),
                BenchSelector.ForCurrentSprint(null), BenchSelector.ForCurrentSprint("fixture display"),
                BenchSelector.ForCurrentSprintCanonical("fixture-account"), configured],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var benches = new SqliteBenchRepository(cache);
        await benches.RemoveSelectorAsync(evidence.Bench.Id, BenchSelector.ForItem(42));
        foreach (var selector in selectors)
            await benches.AddSelectorAsync(evidence.Bench.Id, selector);
        var bench = (await benches.GetAllAsync()).Single();
        evidence = evidence with { Bench = bench };
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        await source.RecordAsync(evidence, snapshot, default);
        var reloaded = source.Read(Key);
        reloaded.Bench.Id.ShouldBe(bench.Id);
        reloaded.Bench.Name.ShouldBe(bench.Name);
        reloaded.Bench.IsDefault.ShouldBe(bench.IsDefault);
        reloaded.Bench.Selectors.ToArray().ShouldBe(bench.Selectors.ToArray());
        var json = JsonSerializer.Serialize(reloaded, TwigJsonContext.Default.DraftCaptureEvidence);
        json.ShouldNotContain("queryRule");
        json.ShouldNotContain("queryAssignedTo");
        var fingerprint = SqliteDraftCaptureEvidenceStore.Fingerprint(evidence);
        SqliteDraftCaptureEvidenceStore.Fingerprint(reloaded).ShouldBe(fingerprint);
        var journal = await CaptureAsync(cache, draft, snapshot, reloaded);
        journal.DraftCapture.ShouldNotBeNull().EvidenceFingerprint.ShouldBe(fingerprint);
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(43));
        var changed = evidence with { Bench = (await benches.GetAllAsync()).Single() };
        SqliteDraftCaptureEvidenceStore.Fingerprint(changed).ShouldNotBe(fingerprint);
        (await Should.ThrowAsync<InvalidOperationException>(() =>
            source.ValidateAsync(draft, snapshot, Origin, fingerprint, default)))
            .Message.ShouldBe("bench-stale: inspected native Bench identity or selectors changed.");
    }

    [Fact]
    public async Task NativeReceiptRefusesCorruptSelectorDiscriminatorAndPayloadWithoutDroppingRules()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (_, _, evidence) = await SeedAsync(cache);
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        void Corrupt(BenchSelector selector) =>
            ReplaceReceipt(cache, evidence with { Bench = evidence.Bench with { Selectors = [selector] } });
        Corrupt(new BenchSelector((SelectorKind)999, "42"));
        Should.Throw<JsonException>(() => source.Read(Key)).Message
            .ShouldBe("Native inspection Bench selector kind is unknown.");
        Corrupt(new BenchSelector(SelectorKind.Item, "not-an-id"));
        Should.Throw<FormatException>(() => source.Read(Key));
        Corrupt(new BenchSelector(SelectorKind.Subtree, "not-an-id"));
        Should.Throw<FormatException>(() => source.Read(Key));
        Corrupt(new BenchSelector(SelectorKind.Query, "unknown-rule"));
        Should.Throw<InvalidOperationException>(() => source.Read(Key)).Message
            .ShouldBe("Unknown Bench query rule 'unknown-rule'; no scope was widened.");
        ReplaceReceipt(cache, evidence);
        var validJson = JsonSerializer.Serialize(evidence, TwigJsonContext.Default.DraftCaptureEvidence);
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = "UPDATE metadata SET value=@value WHERE key LIKE 'draft-served-evidence:%';";
        foreach (var invalidJson in new[]
        {
            validJson.Replace("\"payload\":\"42\"", "\"payload\":null", StringComparison.Ordinal),
            validJson.Replace("\"kind\":0,", "", StringComparison.Ordinal),
        })
        {
            invalidJson.ShouldNotBe(validJson);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@value", invalidJson);
            command.ExecuteNonQuery().ShouldBe(1);
            Should.Throw<JsonException>(() => source.Read(Key));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("corrupt")]
    [InlineData("process")]
    [InlineData("process-corrupt")]
    [InlineData("type-config-missing")]
    [InlineData("snapshot")]
    [InlineData("type")]
    [InlineData("origin")]
    [InlineData("rules-unknown")]
    [InlineData("empty-served")]
    [InlineData("original")]
    [InlineData("null-is-not-empty")]
    [InlineData("capability")]
    [InlineData("state-null")]
    [InlineData("state-gate")]
    [InlineData("state-unknown")]
    [InlineData("state-current-unknown")]
    [InlineData("current-type")]
    [InlineData("bench-drift")]
    [InlineData("bench-nonmember")]
    public async Task UnknownOrChangedNativeEvidenceRefusesWithoutJournalOrIntentLoss(string fault)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var field = fault.StartsWith("state", StringComparison.Ordinal) ? "System.State"
            : fault == "null-is-not-empty" ? "System.AssignedTo"
            : fault == "capability" ? "Custom.Unsupported" : "System.Title";
        var original = field == "System.State" ? "Before" : fault == "null-is-not-empty" ? "" : "baseline";
        var proposed = fault == "state-null" ? null : field == "System.State" ? "After" : "intent";
        var (draft, snapshot, evidence) = await SeedAsync(cache, field, original, proposed);
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        snapshot.TypeName.ShouldBe("FixtureType");
        snapshot.State.ShouldBe("Before");
        snapshot.Revision.ShouldBe(draft.BaselineRevision);
        draft.HasConflicts.ShouldBeFalse();
        SqliteDraftCaptureEvidenceStore.Fingerprint(source.Read(Key))
            .ShouldBe(SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));
        await source.ValidateBenchAsync(evidence, default);
        var controls = WorkItemDetailProjector.CarriedControls(WorkItemDetailProjector.Project(evidence.Layout, snapshot)).ToArray();
        controls.ShouldContain(control => control.Id == field);
        WorkItemDetailProjector.TryGetRawValue(snapshot, field, out var raw).ShouldBeTrue();
        raw.ShouldBe(fault == "null-is-not-empty" ? null : original);
        draft.Changes.Single().BaselineValue.ShouldBe(original);
        draft.Changes.Single().ProposedValue.ShouldBe(proposed);
        snapshot.Fields.ContainsKey("Custom.Gate").ShouldBeFalse();
        if (fault is not ("original" or "null-is-not-empty" or "capability" or "state-null"))
            await source.ValidateAsync(draft, snapshot, Origin, SqliteDraftCaptureEvidenceStore.Fingerprint(evidence), default);
        if (fault == "capability")
            (new EditCapability(new PendingChangeStoreSink(new SqlitePendingChangeStore(cache)),
                WorkItemType.Parse(snapshot.TypeName).Value, false).Validate(new FieldEdit(field, original, proposed))
                is Rejected).ShouldBeTrue();
        if (fault == "version") evidence = evidence with { Version = 9 };
        if (fault == "type") evidence = evidence with { TypeName = "Other" };
        if (fault == "origin") evidence = evidence with { Origin = Origin with { IdentityId = "other" } };
        if (fault == "rules-unknown") evidence = evidence with { Rules = null };
        if (fault == "empty-served") evidence = evidence with { Layout = evidence.Layout with { SystemControls = [] } };
        if (fault == "state-gate") evidence = evidence with
        {
            Rules = [new([new("when", "System.State", "After")], [new("makeRequired", "Custom.Gate", null)], false)],
        };
        // Write malformed observations only as adversarial local-store corruption.
        ReplaceReceipt(cache, evidence);
        if (fault == "missing") source.Invalidate(Key);
        if (fault == "corrupt") Execute(cache, "UPDATE metadata SET value='broken' WHERE key LIKE 'draft-served-evidence:%';");
        if (fault == "process") Execute(cache, "UPDATE process_types SET icon_id='different';");
        if (fault == "process-corrupt")
        {
            Execute(cache, "UPDATE process_types SET states_json='broken';");
            evidence = evidence with { ProcessFingerprint = source.ProcessFingerprint() };
            ReplaceReceipt(cache, evidence);
        }
        if (fault == "type-config-missing")
        {
            Execute(cache, "DELETE FROM process_types;");
            evidence = evidence with { ProcessFingerprint = source.ProcessFingerprint() };
            ReplaceReceipt(cache, evidence);
        }
        if (fault == "current-type")
        {
            Execute(cache, "UPDATE process_types SET type_name='DifferentType';");
            evidence = evidence with { ProcessFingerprint = source.ProcessFingerprint() };
            ReplaceReceipt(cache, evidence);
        }
        if (fault is "state-unknown" or "state-current-unknown")
        {
            await new SqliteProcessTypeStore(cache).SaveAsync(new ProcessTypeRecord
            {
                TypeName = "FixtureType", States = fault == "state-unknown"
                    ? [new("Before", StateCategory.Proposed, null)] : [new("After", StateCategory.Completed, null)],
            });
            evidence = evidence with { ProcessFingerprint = source.ProcessFingerprint() };
            ReplaceReceipt(cache, evidence);
        }
        if (fault is "bench-drift" or "bench-nonmember")
        {
            Execute(cache, "DELETE FROM pending.bench_selectors;");
            if (fault == "bench-nonmember")
            {
                evidence = evidence with { Bench = evidence.Bench with { Selectors = [] } };
                ReplaceReceipt(cache, evidence);
            }
        }
        if (fault == "snapshot")
        {
            var repository = new SqliteWorkItemRepository(cache, new WorkItemMapper());
            await repository.SaveAsync(new WorkItemMapper().Map(snapshot with { Title = "same revision changed value" }));
            snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        }
        if (fault == "original")
        {
            draft = new RecoverableWorkItemDraft(draft.ItemKey, 5, 5, draft.Generation + 1,
                [new(field, "wrong original", proposed)], []);
            (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(draft, draft.Generation - 1)).ShouldBeTrue();
        }
        var exception = await Record.ExceptionAsync(async () => await CaptureAsync(cache, draft, snapshot, evidence));
        exception.ShouldNotBeNull();
        if (fault is "corrupt" or "process-corrupt")
        {
            exception.ShouldBeAssignableTo<JsonException>();
            exception.Message.ShouldContain("'b' is an invalid start of a value");
        }
        else
        {
            exception.ShouldBeOfType<InvalidOperationException>();
            exception.Message.ShouldBe(fault switch
            {
                "missing" => "metadata-unavailable: explicitly inspect this draft's native served form first.",
                "version" => "metadata-unavailable: native served evidence version or identity is invalid.",
                "type-config-missing" => "metadata-unavailable: native process type catalog is empty.",
                "current-type" => "metadata-unavailable: process configuration does not contain this native type.",
                "rules-unknown" => "metadata-unavailable: native process rules were not observed.",
                "empty-served" => "field-not-carried: field is not carried by the served form.",
                "original" or "null-is-not-empty" => "field-baseline-mismatch: exact original field value changed.",
                "capability" => "capability-refused: Field 'Custom.Unsupported' is not persistable by this sink.",
                "state-null" => "rules-refused: System.State cannot be null.",
                "state-unknown" or "state-current-unknown" => "state-unknown: current or proposed state is absent from the observed native type catalog.",
                "state-gate" => "rules-refused: Refusing to write work item 42: an enabled process rule requires field 'Custom.Gate' when state is 'After', but the effective value is empty.",
                "bench-drift" => "bench-stale: inspected native Bench identity or selectors changed.",
                "bench-nonmember" => "bench-item-not-member: item is not in the inspected native Bench's local membership.",
                _ => "metadata-stale: qualified native observation or local schema identity changed.",
            });
        }
        Count(cache, "proposal_journals").ShouldBe(0);
        Count(cache, "proposal_operations").ShouldBe(0);
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Changes.ShouldBe(draft.Changes);
    }

    [Fact]
    public async Task SequentialReceiptReplacementRefusesAndFreshnessRetainsOriginalBinding()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache);
        var journal = await CaptureAsync(cache, draft, snapshot, evidence);
        var replacement = evidence with { Rules = [new([], [], false, Name: "new observed rules")] };
        ReplaceReceipt(cache, replacement);
        var journals = new SqlitePlanJournalRepository(cache);
        (await journals.CaptureIssueAsync(journal, Origin, default)).Code.ShouldBe("draft-capture-stale");
        (await Should.ThrowAsync<InvalidOperationException>(() => CaptureAsync(cache, draft, snapshot, evidence)))
            .Message.ShouldBe("metadata-stale: qualified native observation or local schema identity changed.");
        Count(cache, "proposal_journals").ShouldBe(1);
        (await journals.GetAsync(journal.Digest))!.DraftCapture!.EvidenceFingerprint.ShouldBe(journal.DraftCapture!.EvidenceFingerprint);
        var next = await CaptureAsync(cache, draft, snapshot, replacement);
        next.Digest.ShouldNotBe(journal.Digest);
    }

    [Fact]
    public async Task ProviderNeutralStatusRetainsCaptureWhenNativeOriginUnavailable()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache);
        var captured = await CaptureAsync(cache, draft, snapshot, evidence);
        var journals = new SqlitePlanJournalRepository(cache);
        Directory.CreateDirectory(Path.Combine(_directory, "proposals"));
        await File.WriteAllTextAsync(captured.SourcePath, captured.CanonicalJson);
        journals.SetCaptureExportState(captured.Digest, complete: true);
        var before = await journals.GetAsync(captured.Digest);
        before.ShouldNotBeNull();
        before.State.ShouldBe(PlanOperationState.Planned);
        before.AuthorizationMode.ShouldBeNull();
        before.AuthorizerIdentity.ShouldBeNull();
        before.AuthorizedAt.ShouldBeNull();
        before.ConfirmedAt.ShouldBeNull();
        before.DraftCapture.ShouldNotBeNull();
        var fileBefore = await File.ReadAllBytesAsync(before.SourcePath);
        var journalBefore = ReadRows("SELECT * FROM pending.proposal_journals;");
        var operationsBefore = ReadRows("SELECT * FROM pending.proposal_operations;");
        var metadataBefore = ReadRows("SELECT * FROM metadata ORDER BY key;");
        var draftBefore = JsonSerializer.Serialize(draft, TwigJsonContext.Default.RecoverableWorkItemDraft);
        var twigDir = Path.Combine(_directory, ".twig");
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(_directory, "twig.db"));
        var providerSnapshots = Substitute.For<IWorkItemSnapshotReader>();
        var lifecycle = new PlanLifecycleService(new PlanDocumentParser(), journals,
            new SqlitePendingChangeStore(cache), new SqliteWorkItemRepository(cache, new WorkItemMapper()),
            new TwigConfiguration { Organization = Workspace.Organization, Project = Workspace.Project },
            paths, providerSnapshots);

        var status = await lifecycle.StatusAsync(before.SourcePath);

        status.ShouldNotBeNull();
        status.Found.ShouldBeTrue();
        status.Digest.ShouldBe(before.Digest);
        var issue = status.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe("draft-capture-stale");
        issue.Path.ShouldBe(string.Empty);
        issue.Message.ShouldBe("Native capture origin is unavailable in this provider composition.");
        providerSnapshots.ReceivedCalls().ShouldBeEmpty();
        ReadRows("SELECT * FROM pending.proposal_journals;").ShouldBe(journalBefore);
        ReadRows("SELECT * FROM pending.proposal_operations;").ShouldBe(operationsBefore);
        ReadRows("SELECT * FROM metadata ORDER BY key;").ShouldBe(metadataBefore);
        (await File.ReadAllBytesAsync(before.SourcePath)).ShouldBe(fileBefore);
        var retainedDraft = await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key);
        retainedDraft.ShouldNotBeNull();
        JsonSerializer.Serialize(retainedDraft, TwigJsonContext.Default.RecoverableWorkItemDraft).ShouldBe(draftBefore);

        string[] ReadRows(string sql)
        {
            using var command = cache.GetConnection().CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "<null>"
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
            return rows.ToArray();
        }
    }

    [Fact]
    public async Task CaptureImmediateTransactionBlocksCompetingReceiptReplacementAtJournalInsertBoundary()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, evidence) = await SeedAsync(cache);
        using var competitor = new SqliteConnection(ConnectionString);
        competitor.Open();
        using (var timeout = competitor.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout=1;";
            timeout.ExecuteNonQuery();
        }
        var attempted = false;
        int? refusedCode = null;
        cache.GetConnection().CreateFunction("attempt_receipt_replace", () =>
        {
            attempted = true;
            cache.ActiveTransaction.ShouldNotBeNull();
            using var update = competitor.CreateCommand();
            update.CommandTimeout = 1;
            update.CommandText = "UPDATE metadata SET value='competing writer' WHERE key LIKE 'draft-served-evidence:%';";
            try { update.ExecuteNonQuery(); }
            catch (SqliteException ex) { refusedCode = ex.SqliteErrorCode; }
            return 0;
        });
        Execute(cache, """
            CREATE TEMP TRIGGER capture_boundary BEFORE INSERT ON pending.proposal_journals
            BEGIN SELECT attempt_receipt_replace(); END;
            """);
        var journal = await CaptureAsync(cache, draft, snapshot, evidence);
        attempted.ShouldBeTrue();
        refusedCode.ShouldBe(5);
        SqliteDraftCaptureEvidenceStore.Fingerprint(new SqliteDraftCaptureEvidenceStore(cache).Read(Key))
            .ShouldBe(SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));
        journal.DraftCapture!.EvidenceFingerprint.ShouldBe(SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));
        Count(cache, "proposal_journals").ShouldBe(1);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("locked")]
    [InlineData("unavailable")]
    public async Task ExplicitInspectionBypassesBothWarmNativeLayoutCachesAndNeverRetainsOldServed(string outcome)
    {
        using var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/projects/",
            """{"capabilities":{"processTemplate":{"templateTypeId":"observed-process"}}}""");
        handler.SetProcessWorkItemTypesResponse(("FixtureType", "Observed.Type"));
        handler.SetRawResponse("/layout",
            """{"pages":[],"systemControls":[{"id":"System.Title","controlType":"FieldControl","visible":true}]}""");
        handler.SetRawResponse("/rules", """{"value":[]}""");
        var provider = FakeHandler.CreateService(handler);
        var source = new DetailDocumentSource(provider);
        var item = new WorkItem { Id = 42, Type = WorkItemType.Parse("FixtureType").Value, Title = "baseline" };
        await source.GetAsync(item);
        source.ServedLayout("FixtureType").ShouldNotBeNull();
        (await provider.GetFormLayoutAsync("FixtureType")).ShouldBeOfType<FormLayoutResult.Served>();
        handler.SetStatusResponse("/layout", outcome == "locked" ? HttpStatusCode.BadRequest
            : outcome == "failed" ? HttpStatusCode.InternalServerError : HttpStatusCode.NotFound,
            outcome == "locked" ? """{"message":"VS403115 locked type"}""" : """{"message":"inspection error"}""");
        if (outcome == "failed")
        {
            var error = await Record.ExceptionAsync(() => source.InspectAsync("FixtureType", provider, default));
            error.ShouldNotBeNull();
            error.Message.ShouldContain("inspection error");
        }
        else
            (await source.InspectAsync("FixtureType", provider, default)).ShouldBeNull();
        source.ServedLayout("FixtureType").ShouldBeNull();
        // The ordinary provider cache also must not return the old positive observation.
        if (outcome != "failed")
            ((await provider.GetFormLayoutAsync("FixtureType")) is FormLayoutResult.Served).ShouldBeFalse();
    }

    [Fact]
    public async Task ExplicitInspectionRefreshesProcessIdentityAndRulesTogetherWithoutChangingOrdinaryCacheBehavior()
    {
        using var handler = new InspectionHandler();
        handler.SetRawResponse("/_apis/projects/",
            """{"capabilities":{"processTemplate":{"templateTypeId":"old-process"}}}""");
        handler.SetWorkItemTypesResponse("FixtureType");
        handler.SetProcessWorkItemTypesResponse(("FixtureType", "Observed.Type"));
        handler.SetRawResponse("/layout", """{"pages":[]}""");
        handler.SetRawResponse("/rules", """{"value":[]}""");
        var provider = FakeHandler.CreateService(handler);
        var source = new DetailDocumentSource(provider);
        var item = new WorkItem { Id = 42, Type = WorkItemType.Parse("FixtureType").Value, Title = "baseline" };
        await source.GetAsync(item);
        await provider.GetRulesAsync("FixtureType");
        source.ServedLayout("FixtureType")!.ProcessId.ShouldBe("old-process");
        var warm = handler.Paths.Count;
        await source.GetAsync(item);
        await provider.GetRulesAsync("FixtureType");
        handler.Paths.Count.ShouldBe(warm);
        handler.SetRawResponse("/_apis/projects/",
            """{"capabilities":{"processTemplate":{"templateTypeId":"fresh-process"}}}""");
        (await source.InspectAsync("FixtureType", provider, default)).ShouldNotBeNull();
        source.ServedLayout("FixtureType")!.ProcessId.ShouldBe("fresh-process");
        var fresh = handler.Paths.Skip(warm).ToArray();
        fresh.ShouldContain(path => path.EndsWith("/fresh-process/workItemTypes/Observed.Type/layout", StringComparison.Ordinal));
        fresh.ShouldContain(path => path.EndsWith("/fresh-process/workItemTypes/Observed.Type/rules", StringComparison.Ordinal));
        fresh.ShouldNotContain(path => path.Contains("old-process", StringComparison.Ordinal));
        var observed = handler.Paths.Count;
        await source.GetAsync(item);
        await provider.GetRulesAsync("FixtureType");
        handler.Paths.Count.ShouldBe(observed);
    }

    private sealed class InspectionHandler : FakeHandler
    {
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, ct);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task ExplicitNativeInspectionRefusesUnknownRulePayloadRatherThanObservingEmpty(string payload)
    {
        using var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/projects/",
            """{"capabilities":{"processTemplate":{"templateTypeId":"observed-process"}}}""");
        handler.SetProcessWorkItemTypesResponse(("FixtureType", "Observed.Type"));
        handler.SetRawResponse("/layout", """{"pages":[]}""");
        handler.SetRawResponse("/rules", payload == "null" ? """{"value":null}"""
            : payload == "missing" ? "{}" : "broken");
        var provider = FakeHandler.CreateService(handler);
        var source = new DetailDocumentSource(provider);
        await source.GetAsync(new WorkItem { Id = 42, Type = WorkItemType.Parse("FixtureType").Value });
        source.ServedLayout("FixtureType").ShouldNotBeNull();
        var error = await Record.ExceptionAsync(() => source.InspectAsync("FixtureType", provider, default));
        error.ShouldNotBeNull();
        source.ServedLayout("FixtureType").ShouldBeNull();
        if (payload == "malformed")
        {
            error.ShouldBeAssignableTo<JsonException>();
            error.Message.ShouldContain("'b' is an invalid start of a value");
        }
        else
        {
            error.ShouldBeOfType<InvalidOperationException>();
            error.Message.ShouldBe("metadata-unavailable: explicit process rules response has no rule set.");
        }
    }

    [Fact]
    public async Task LegacyCaptureRemainsInspectableWithoutEvidencePromotion()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot, _) = await SeedAsync(cache);
        var journal = await new SqliteDraftProposalCaptureStore(cache).CaptureAsync(draft, snapshot,
            Origin, Workspace, Path.Combine(_directory, "proposals"));
        var json = JsonSerializer.Serialize(journal.DraftCapture, TwigJsonContext.Default.DraftProposalCapture);
        var oldJson = json.Replace(",\"evidenceFingerprint\":null", "", StringComparison.Ordinal);
        oldJson.ShouldNotContain("evidenceFingerprint");
        var old = JsonSerializer.Deserialize(oldJson, TwigJsonContext.Default.DraftProposalCapture);
        old.ShouldNotBeNull();
        old.Version.ShouldBe(1);
        old.EvidenceFingerprint.ShouldBeNull();
        (await new SqlitePlanJournalRepository(cache).CaptureIssueAsync(journal, Origin, default))
            .Code.ShouldBe("draft-capture-missing-evidence");
    }

    [Fact]
    public async Task InspectionCompareAndSaveRefusesChangedProcessInputsAndInvalidatesOnlyExactKey()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (_, snapshot, evidence) = await SeedAsync(cache);
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        var sibling = evidence with { ItemKey = new(_directory, "connection", "binding", 43) };
        ReplaceReceipt(cache, sibling);
        Execute(cache, "UPDATE process_types SET icon_id='changed';");
        (await Should.ThrowAsync<InvalidOperationException>(() => source.RecordAsync(evidence, snapshot, default)))
            .Message.ShouldBe("metadata-stale: native inspection baseline, process inputs or origin changed.");
        source.Invalidate(Key);
        source.Read(sibling.ItemKey).ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => source.Read(Key));
    }

    private async Task<(RecoverableWorkItemDraft, WorkItemSnapshot, DraftCaptureEvidence)> SeedAsync(
        SqliteCacheStore cache, string field = "System.Title", string? original = "baseline", string? proposed = "intent",
        bool recordEvidence = true)
    {
        var repository = new SqliteWorkItemRepository(cache, new WorkItemMapper());
        await repository.SaveAsync(new WorkItemMapper().Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 5, TypeName = "FixtureType", Title = "baseline", State = "Before",
            Fields = new Dictionary<string, string?> { ["Custom.Unsupported"] = "baseline" },
        }));
        await new SqliteProcessTypeStore(cache).SaveAsync(new ProcessTypeRecord
        {
            TypeName = "FixtureType", States = [new("Before", StateCategory.Proposed, null), new("After", StateCategory.Completed, null)],
        });
        var snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(Key, 5).SetField(field, original, proposed);
        (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(draft, null)).ShouldBeTrue();
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        var benches = new SqliteBenchRepository(cache);
        var bench = await benches.CreateAsync("inspected");
        bench.ShouldNotBeNull();
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        bench = (await benches.GetAllAsync()).Single();
        var evidence = new DraftCaptureEvidence(1, Key, Origin, null, snapshot.TypeName, 5,
            source.ProcessFingerprint(), SqliteDraftCaptureEvidenceStore.SnapshotFingerprint(snapshot),
            new FormLayout("Fixture.Reference", "process", [])
            {
                SystemControls = new[] { "System.Title", "System.AssignedTo", "System.State", "Custom.Unsupported" }
                    .Select(name => new LayoutControl(name, name, "FieldControl", false, true, false)).ToArray(),
            }, [], bench, null);
        if (recordEvidence) await source.RecordAsync(evidence, snapshot, default);
        return (draft, snapshot, evidence);
    }

    private Task<PlanJournal> CaptureAsync(SqliteCacheStore cache, RecoverableWorkItemDraft draft,
        WorkItemSnapshot snapshot, DraftCaptureEvidence evidence)
        => new SqliteDraftProposalCaptureStore(cache).CaptureObservedAsync(draft, snapshot, Origin, Workspace,
            Path.Combine(_directory, "proposals"), SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));

    private static void ReplaceReceipt(SqliteCacheStore cache, DraftCaptureEvidence evidence)
    {
        var key = "draft-served-evidence:" + PlanCanonicalizer.Canonicalize(
            JsonSerializer.SerializeToElement(evidence.ItemKey, TwigJsonContext.Default.QualifiedWorkItemKey)).Digest;
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO metadata(key,value) VALUES(@key,@value);";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(evidence, TwigJsonContext.Default.DraftCaptureEvidence));
        command.ExecuteNonQuery();
    }

    private static int Count(SqliteCacheStore cache, string table)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pending.{table};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static void Execute(SqliteCacheStore cache, string sql)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
