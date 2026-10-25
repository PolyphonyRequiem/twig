using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class RecoverableDraftEditingTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    private readonly LayoutProvider _layout = new();
    private readonly RuleProvider _rules = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationTheory]
    [InlineData(null, "")]
    [InlineData("Original owner", null)]
    public async Task RetainBindsExactNativeIntentAndDigestWithoutPublicationOrExportAcrossCompositions(
        string? assignedBaseline, string? assignedProposed)
    {
        var runtime = await SeedAsync();
        if (assignedBaseline is not null)
        {
            var repository = runtime.GetRequiredService<IWorkItemRepository>();
            var mapper = new WorkItemMapper();
            var snapshot = mapper.ToSnapshot((await repository.GetByIdAsync(42))!);
            await repository.SaveAsync(mapper.Map(snapshot with { AssignedTo = assignedBaseline }));
        }
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!,
            [new("System.Title", "Intent"), new("System.AssignedTo", assignedProposed)]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var document = staged.Document!;
        _layout.Refuse = _rules.Refuse = true;
        _fixture.Transport.RejectMetadata = true;
        var before = _fixture.Transport.SnapshotHttp();
        var metadataBefore = ReadMetadata(runtime);
        var request = RetainRequest(document);
        // RETAIN resolves the production DI graph; only OPEN's external form/rules boundary was supplied above.
        var production = runtime.GetRequiredService<RecoverableDraftEditingModule>();
        var retained = await production.RetainAsync(request, Workspace(runtime));
        retained.Error.ShouldBeNull(retained.Error?.Message);
        retained.Status.ShouldBe("retained");
        retained.Committed.ShouldBeTrue();
        retained.Document.ShouldBeNull();
        var receipt = retained.Capture.ShouldNotBeNull();
        receipt.NativeContext.ShouldBe(document.NativeContext);
        receipt.Generation.ShouldBe(document.Generation);
        receipt.BaselineRevision.ShouldBe(document.BaselineRevision);
        receipt.ObservedRevision.ShouldBe(document.ObservedRevision);
        receipt.CurrentRevision.ShouldBe(document.CurrentRevision);
        receipt.OriginCoverage.ShouldBe("editing-context-and-native-origin-digest");
        receipt.Authorized.ShouldBeFalse();
        receipt.CanApply.ShouldBeFalse();
        receipt.Effects.ShouldBe(document.Changes);
        receipt.Effects.Single(effect => effect.FieldRef == "System.AssignedTo").BaselineValue.ShouldBe(assignedBaseline);
        receipt.Effects.Single(effect => effect.FieldRef == "System.AssignedTo").ProposedValue.ShouldBe(assignedProposed);
        var parsed = new PlanDocumentParser().Parse(receipt.CanonicalJson);
        parsed.IsValid.ShouldBeTrue();
        parsed.Digest.ShouldBe(receipt.Digest);
        parsed.CanonicalJson.ShouldBe(receipt.CanonicalJson);
        var batch = parsed.Plan!.Operations.Single().ShouldBeOfType<BatchOperation>();
        batch.Id.ShouldBe(receipt.OperationId);
        batch.WorkItemId.ShouldBe(42);
        batch.ExpectedRevision.ShouldBe(3);
        batch.Fields.Count.ShouldBe(receipt.Effects.Count);
        foreach (var effect in receipt.Effects) batch.Fields[effect.FieldRef].ShouldBe(effect.ProposedValue);
        var journals = runtime.GetRequiredService<IPlanJournalRepository>();
        var journal = (await journals.GetAsync(receipt.Digest)).ShouldNotBeNull();
        journal.DraftCapture.ShouldNotBeNull().Draft.Generation.ShouldBe(long.Parse(document.Generation));
        journal.DraftCapture.EvidenceFingerprint.ShouldBe(document.NativeContext.EvidenceDigest);
        journal.State.ShouldBe(PlanOperationState.Planned);
        journal.AuthorizationMode.ShouldBeNull();
        journal.AuthorizerIdentity.ShouldBeNull();
        journal.AuthorizedAt.ShouldBeNull();
        journal.ReviewModelJson.ShouldBeNull();
        journal.ConfirmedAt.ShouldBeNull();
        journal.CompletedAt.ShouldBeNull();
        journal.Operations.Single().State.ShouldBe(PlanOperationState.Planned);
        File.Exists(journal.SourcePath).ShouldBeFalse();
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
        var fresh = await _fixture.CreateRuntimeAsync();
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
        var replay = await fresh.GetRequiredService<RecoverableDraftEditingModule>().RetainAsync(request, Workspace(fresh));
        replay.Error.ShouldBeNull(replay.Error?.Message);
        replay.Committed.ShouldBeTrue();
        replay.Capture!.Digest.ShouldBe(receipt.Digest);
        replay.Capture.OperationId.ShouldBe(receipt.OperationId);
        (await journals.GetAsync(receipt.Digest))!.PreviewedAt.ShouldBe(journal.PreviewedAt);
        (await journals.GetAsync(receipt.Digest))!.LastPreviewedAt.ShouldBe(journal.LastPreviewedAt);
        (await journals.GetUnresolvedAsync()).Count.ShouldBe(1);
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Original");
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey))!.Generation.ShouldBe(long.Parse(document.Generation));
        _fixture.Transport.SnapshotHttp().ShouldBe(before with
        {
            Total = before.Total + 1,
            Principal = before.Principal + 1,
        });
        ReadMetadata(runtime).ShouldBe(metadataBefore, "Retaining intent must not retarget active-item or Bench metadata.");
        var suffix = assignedProposed is null ? "-clear" : "";
        CaptureEnvelope("retained" + suffix, retained);
        CaptureJson("retain-request" + suffix, JsonSerializer.Serialize(request, TwigJsonContext.Default.DraftRetainRequest));
    }

    [HostMigrationTheory]
    [InlineData("generation", "draft-cas-conflict")]
    [InlineData("baseline", "revision-conflict")]
    [InlineData("observed", "revision-conflict")]
    [InlineData("snapshot", "revision-conflict")]
    [InlineData("origin", "authority-invalidated")]
    [InlineData("identity", "authority-invalidated")]
    [InlineData("key", "authority-invalidated")]
    [InlineData("evidence", "authority-invalidated")]
    [InlineData("native-evidence", "metadata-unavailable")]
    [InlineData("native-process", "metadata-stale")]
    [InlineData("pending", "capture-unavailable")]
    public async Task ProductionRetainRefusesStaleInputsWithoutDiscardingIntentOrWritingJournal(string fault, string code)
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "retained intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var document = staged.Document!;
        var request = RetainRequest(document);
        request = fault switch
        {
            "generation" => request with { Generation = opened.Document!.Generation },
            "baseline" => request with { BaselineRevision = "2" },
            "observed" => request with { ObservedRevision = "2" },
            "origin" => request with { NativeContext = document.NativeContext with { OriginDigest = "different" } },
            "identity" => request with { NativeContext = document.NativeContext with { IdentityId = "different" } },
            "key" => request with { NativeContext = document.NativeContext with
                { ItemKey = new(document.NativeContext.ItemKey.CheckoutRoot, "different",
                    document.NativeContext.ItemKey.BindingId, 42) } },
            "evidence" => request with { NativeContext = document.NativeContext with { EvidenceDigest = "different" } },
            _ => request,
        };
        var cache = runtime.GetRequiredService<SqliteCacheStore>();
        if (fault == "snapshot")
        {
            var changed = NativeBrowserFixture.Item(42, "remote");
            changed.MarkSynced(4);
            await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(changed);
        }
        if (fault == "native-evidence")
            new SqliteDraftCaptureEvidenceStore(cache).Invalidate(document.NativeContext.ItemKey);
        if (fault == "native-process")
        {
            using var command = cache.GetConnection().CreateCommand();
            command.CommandText = "UPDATE process_types SET icon_id='changed';";
            command.ExecuteNonQuery().ShouldBeGreaterThan(0);
        }
        if (fault == "pending")
        {
            using var command = cache.GetConnection().CreateCommand();
            command.CommandText = """
                INSERT INTO pending.pending_changes(work_item_id,change_type,field_name,old_value,new_value,created_at)
                VALUES(99,'FieldChange','System.Title','before','pending','fixed');
                """;
            command.ExecuteNonQuery().ShouldBe(1);
        }
        var before = _fixture.Transport.SnapshotHttp();
        var result = await runtime.GetRequiredService<RecoverableDraftEditingModule>()
            .RetainAsync(request, Workspace(runtime));
        result.Status.ShouldBe("refused");
        result.Committed.ShouldBeFalse();
        result.Error.ShouldNotBeNull().Code.ShouldBe(code);
        result.Capture.ShouldBeNull();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        var intent = (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey)).ShouldNotBeNull();
        intent.Generation.ShouldBe(long.Parse(document.Generation));
        intent.Changes.Single().ProposedValue.ShouldBe("retained intent");
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task EmptyAndRestoredBaselineIntentNeverRetainsAnEmptyBatch()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var production = runtime.GetRequiredService<RecoverableDraftEditingModule>();
        (await production.RetainAsync(RetainRequest(opened.Document!), Workspace(runtime)))
            .Error.ShouldNotBeNull().Code.ShouldBe("draft-empty");
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var restored = await module.StageAsync(Request(staged.Document!, [new("System.Title", "Original")]));
        restored.Error.ShouldBeNull(restored.Error?.Message);
        restored.Document!.Generation.ShouldBe("2");
        restored.Document.Changes.ShouldBeEmpty();
        var result = await production.RetainAsync(RetainRequest(restored.Document), Workspace(runtime));
        result.Error.ShouldNotBeNull().Code.ShouldBe("draft-empty");
        result.Committed.ShouldBeFalse();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task ProductionRetainRefusesIntentMadeReadOnlyByAnExplicitFreshNativeForm()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "retained intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        _layout.TitleReadOnly = true;
        var observed = await module.OpenAsync(42, "default");
        observed.Error.ShouldBeNull(observed.Error?.Message);
        observed.Document!.Fields.Single(field => field.FieldRef == "System.Title").Editable.ShouldBeFalse();
        var result = await runtime.GetRequiredService<RecoverableDraftEditingModule>()
            .RetainAsync(RetainRequest(observed.Document), Workspace(runtime));
        result.Error.ShouldNotBeNull().Code.ShouldBe("field-unavailable");
        result.Committed.ShouldBeFalse();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(observed.Document.NativeContext.ItemKey))!.Changes.Single().ProposedValue.ShouldBe("retained intent");
    }

    [HostMigrationFact]
    public async Task RetainCancellationDuringInsertRollsBackAndPostCommitFenceReportsDurableCapture()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var cache = runtime.GetRequiredService<SqliteCacheStore>();
        using var canceled = new CancellationTokenSource();
        cache.GetConnection().CreateFunction("retain_probe", () => { canceled.Cancel(); return 0; });
        using (var command = cache.GetConnection().CreateCommand())
        {
            command.CommandText = """
                CREATE TEMP TRIGGER retain_probe BEFORE INSERT ON pending.proposal_journals
                BEGIN SELECT retain_probe(); END;
                """;
            command.ExecuteNonQuery();
        }
        var production = runtime.GetRequiredService<RecoverableDraftEditingModule>();
        var canceledResult = await production.RetainAsync(RetainRequest(staged.Document!), Workspace(runtime), canceled.Token);
        canceledResult.Error.ShouldNotBeNull().Code.ShouldBe("canceled");
        canceledResult.Committed.ShouldBeFalse();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        cache.GetConnection().CreateFunction("retain_probe", () =>
        {
            cache.ActiveTransaction.ShouldNotBeNull();
            using var command = cache.GetConnection().CreateCommand();
            command.Transaction = cache.ActiveTransaction;
            command.CommandText = "UPDATE benches SET name='changed-during-retention';";
            command.ExecuteNonQuery().ShouldBe(1);
            return 0;
        });
        var result = await production.RetainAsync(RetainRequest(staged.Document!), Workspace(runtime));
        result.Status.ShouldBe("indeterminate");
        result.Committed.ShouldBeTrue();
        result.Error.ShouldNotBeNull().Code.ShouldBe("post-commit-fence");
        result.Capture.ShouldNotBeNull();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetAsync(result.Capture.Digest)).ShouldNotBeNull();
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(staged.Document!.NativeContext.ItemKey))!.Changes.Single().ProposedValue.ShouldBe("intent");
    }

    private static PlanWorkspace Workspace(ServiceProvider runtime)
    {
        var config = runtime.GetRequiredService<CommandContext>().Config;
        return new() { Organization = config.Organization, Project = config.Project };
    }

    private static string ReadMetadata(ServiceProvider runtime)
    {
        using var command = runtime.GetRequiredService<SqliteCacheStore>().GetConnection().CreateCommand();
        command.CommandText = """
            SELECT COALESCE(group_concat(key || '=' || COALESCE(value,'<null>'),char(10)),'')
            FROM (SELECT key,value FROM metadata ORDER BY key);
            """;
        return (string)command.ExecuteScalar()!;
    }

    private static DraftRetainRequest RetainRequest(DraftEditingDocument document)
        => new(1, document.NativeContext, document.Generation, document.BaselineRevision, document.ObservedRevision);

    [HostMigrationFact]
    public async Task RetainCommandEmitsExactJsonSuccessAndNonzeroRefusalEnvelopes()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var request = RetainRequest(staged.Document!);
        var output = new StringWriter();
        var error = new StringWriter();
        var ctx = runtime.GetRequiredService<CommandContext>() with { Stderr = error };
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request,
            TwigJsonContext.Default.DraftRetainRequest));
        var command = new DraftEditingCommand(ctx, () => runtime.GetRequiredService<RecoverableDraftEditingModule>(), output, input);
        (await command.RetainAsync(true, "json")).ShouldBe(0);
        error.ToString().ShouldBeEmpty();
        var retained = JsonSerializer.Deserialize(output.ToString(), TwigJsonContext.Default.DraftEditingResult)
            .ShouldNotBeNull();
        retained.Committed.ShouldBeTrue();
        retained.Status.ShouldBe("retained");
        retained.Capture.ShouldNotBeNull().Effects.Single().ProposedValue.ShouldBe("intent");
        using var staleInput = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request with { Generation = "0" },
            TwigJsonContext.Default.DraftRetainRequest));
        output.GetStringBuilder().Clear();
        var staleCommand = new DraftEditingCommand(ctx, () => runtime.GetRequiredService<RecoverableDraftEditingModule>(), output, staleInput);
        (await staleCommand.RetainAsync(true, "json")).ShouldBe(1);
        var refused = JsonSerializer.Deserialize(output.ToString(), TwigJsonContext.Default.DraftEditingResult)
            .ShouldNotBeNull();
        refused.Committed.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().Code.ShouldBe("draft-cas-conflict");
        using var jsonError = JsonDocument.Parse(error.ToString());
        jsonError.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [HostMigrationFact]
    public async Task ExplicitOpenAndNullableAtomicStageShareDraftAcrossCompositionsWithoutPublishing()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        opened.Committed.ShouldBeTrue();
        var document = opened.Document!;
        document.Generation.ShouldBe("0");
        document.NativeContext.ExternalIdentity.ProviderKind.ShouldBe("ado");
        document.Pages.Select(page => page.Id).ShouldBe(["fields", "history"]);
        document.Fields.Single(field => field.FieldRef == "System.Description").Editable.ShouldBeFalse();
        var before = _fixture.Transport.SnapshotHttp();
        _layout.Refuse = _rules.Refuse = true;
        _fixture.Transport.RejectMetadata = true;
        var request = Request(document, [
            new("system.title", "Intent"), new("System.AssignedTo", "")]);
        var staged = await module.StageAsync(request);
        staged.Error.ShouldBeNull(staged.Error?.Message);
        staged.Committed.ShouldBeTrue();
        staged.Document!.Generation.ShouldBe("2");
        staged.Document.Changes.Single(change => change.FieldRef == "System.AssignedTo").BaselineValue.ShouldBeNull();
        staged.Document.Changes.Single(change => change.FieldRef == "System.AssignedTo").ProposedValue.ShouldBe("");
        CaptureEnvelope("opened", opened);
        CaptureEnvelope("staged", staged);
        var requestJson = JsonSerializer.Serialize(request, TwigJsonContext.Default.DraftStageRequest);
        var restoredRequest = JsonSerializer.Deserialize(requestJson, TwigJsonContext.Default.DraftStageRequest);
        restoredRequest.ShouldNotBeNull();
        JsonSerializer.Serialize(restoredRequest, TwigJsonContext.Default.DraftStageRequest).ShouldBe(requestJson);
        CaptureJson("request", requestJson);
        var cleared = await module.StageAsync(Request(staged.Document, [new("System.AssignedTo", null)]));
        cleared.Error.ShouldBeNull(cleared.Error?.Message);
        cleared.Document!.Changes.Single().FieldRef.ShouldBe("System.Title");
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        var fresh = await _fixture.CreateRuntimeAsync();
        var retained = await fresh.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey);
        retained!.Generation.ShouldBe(3);
        retained.Changes.Single().ProposedValue.ShouldBe("Intent");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task WholeBatchRefusesDuplicatesUnsupportedFieldsAndStaleGenerationWithoutPartialWrites()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var document = opened.Document!;
        var duplicate = await module.StageAsync(Request(document, [
            new("System.Title", "first"), new("system.title", "second")]));
        duplicate.Error!.Code.ShouldBe("duplicate-field");
        duplicate.Committed.ShouldBeFalse();
        var unsupported = await module.StageAsync(Request(document, [
            new("System.Title", "first"), new("System.Description", "unsupported")]));
        unsupported.Error!.Code.ShouldBe("field-unavailable");
        unsupported.Committed.ShouldBeFalse();
        var before = await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey);
        before!.Generation.ShouldBe(0);
        before.Changes.ShouldBeEmpty();
        var winner = await module.StageAsync(Request(document, [new("System.Title", "winner")]));
        winner.Error.ShouldBeNull(winner.Error?.Message);
        var loser = await module.StageAsync(Request(document, [new("System.Title", "loser")]));
        loser.Error!.Code.ShouldBe("draft-cas-conflict");
        loser.Committed.ShouldBeFalse();
        var retained = await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey);
        retained!.Changes.Single().ProposedValue.ShouldBe("winner");
    }

    [HostMigrationFact]
    public async Task ObservationRetainsOriginalIntentAndReportsRevisionConflictWithoutRebase()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var staged = await module.StageAsync(Request(opened.Document!, [new("System.Title", "intent")]));
        staged.Error.ShouldBeNull(staged.Error?.Message);
        var changed = NativeBrowserFixture.Item(42, "remote");
        changed.MarkSynced(4);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(changed);
        var refused = await module.StageAsync(Request(staged.Document!, [new("System.Title", "newer")]));
        refused.Error!.Code.ShouldBe("revision-conflict");
        var reopened = await module.OpenAsync(42, "default");
        reopened.Error.ShouldBeNull(reopened.Error?.Message);
        reopened.Document!.BaselineRevision.ShouldBe("3");
        reopened.Document.ObservedRevision.ShouldBe("4");
        reopened.Document.HasConflicts.ShouldBeTrue();
        reopened.Document.Changes.Single().ProposedValue.ShouldBe("intent");
        reopened.Document.Conflicts.Single().ObservedValue.ShouldBe("remote");
        (await module.StageAsync(Request(reopened.Document, [new("System.Title", "do not rebase")]))).Error!
            .Code.ShouldBe("revision-conflict");
    }

    [HostMigrationFact]
    public async Task FailedOpenRollsBackNewEvidenceAndDoesNotInventObservedNull()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var key = opened.Document!.NativeContext.ItemKey;
        var native = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var retained = (await native.GetAsync(key))!.SetField("Custom.NotCarried", "baseline", "intent");
        (await native.TrySaveAsync(retained, 0)).ShouldBeTrue();
        var evidence = new SqliteDraftCaptureEvidenceStore(runtime.GetRequiredService<SqliteCacheStore>());
        var prior = SqliteDraftCaptureEvidenceStore.Fingerprint(evidence.Read(key));
        _layout.TitleReadOnly = true;
        var failed = await module.OpenAsync(42, "default");
        failed.Error!.Code.ShouldBe("field-not-carried");
        failed.Committed.ShouldBeFalse();
        SqliteDraftCaptureEvidenceStore.Fingerprint(evidence.Read(key)).ShouldBe(prior);
        var after = (await native.GetAsync(key))!;
        after.Generation.ShouldBe(retained.Generation);
        after.Conflicts.ShouldBeEmpty();
        after.Changes.Single().ProposedValue.ShouldBe("intent");
    }

    [HostMigrationFact]
    public async Task CancellationAfterNativeCasRollsBackTheEntireDraftWrite()
    {
        var runtime = await SeedAsync();
        var opened = await Module(runtime).OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        using var canceled = new CancellationTokenSource();
        var native = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var module = Module(runtime, new AfterSaveStore(native, () => canceled.Cancel()));
        var result = await module.StageAsync(Request(opened.Document!, [new("System.Title", "not committed")]), canceled.Token);
        result.Committed.ShouldBeFalse();
        result.Error!.Code.ShouldBe("canceled");
        var retained = (await native.GetAsync(opened.Document!.NativeContext.ItemKey))!;
        retained.Generation.ShouldBe(0);
        retained.Changes.ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task PostCommitBenchFenceReportsDurableIntentAsIndeterminateNeverAnUncommittedRefusal()
    {
        var runtime = await SeedAsync();
        var opened = await Module(runtime).OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var cache = runtime.GetRequiredService<SqliteCacheStore>();
        var native = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var module = Module(runtime, new AfterSaveStore(native, () =>
        {
            cache.ActiveTransaction.ShouldNotBeNull();
            using var command = cache.GetConnection().CreateCommand();
            command.Transaction = cache.ActiveTransaction;
            command.CommandText = "UPDATE benches SET name='changed-after-cas' WHERE id=@id;";
            command.Parameters.AddWithValue("@id", opened.Document!.NativeContext.BenchId);
            command.ExecuteNonQuery().ShouldBe(1);
        }));
        var result = await module.StageAsync(Request(opened.Document!, [new("System.Title", "durable intent")]));
        result.Committed.ShouldBeTrue();
        result.Status.ShouldBe("indeterminate");
        result.Error!.Code.ShouldBe("post-commit-fence");
        var retained = (await native.GetAsync(opened.Document!.NativeContext.ItemKey))!;
        retained.Changes.Single().ProposedValue.ShouldBe("durable intent");
    }

    [HostMigrationFact]
    public async Task ExactNativeEligibilityAndMalformedContextAreRefusedWithoutWritingIntent()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        _layout.TitleReadOnly = true;
        var opened = await module.OpenAsync(42, "default");
        opened.Error.ShouldBeNull(opened.Error?.Message);
        var document = opened.Document!;
        document.Fields.Single(field => field.FieldRef == "System.Title").Reason.ShouldNotBeNull().ShouldContain("read-only");
        (await module.StageAsync(Request(document, [new("System.Title", "forbidden")]))).Error!
            .Code.ShouldBe("field-unavailable");
        (await module.StageAsync(Request(document, [new("System.State", null)]))).Error!.Code.ShouldBe("rules-refused");
        (await module.StageAsync(Request(document, [new("System.State", "invented")]))).Error!.Code.ShouldBe("state-unknown");
        var before = _fixture.Transport.SnapshotHttp();
        var valid = Request(document, [new("System.AssignedTo", "")]);
        foreach (var malformed in new[]
        {
            valid with { NativeContext = null! },
            valid with { NativeContext = document.NativeContext with { ItemKey = null! } },
            valid with { NativeContext = document.NativeContext with { ExternalIdentity = null! } },
            valid with { NativeContext = document.NativeContext with { IdentityId = null! } },
            valid with { Changes = null! },
            valid with { Changes = [null!] },
        })
            (await module.StageAsync(malformed)).Error!.Code.ShouldBe("usage");
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
        var unsupported = Substitute.For<IWorkItemProvider>();
        unsupported.Capabilities.Returns(ProviderCapabilityCatalog.GitHub);
        (await Module(runtime, provider: unsupported).OpenAsync(42, "default")).Error!.Code.ShouldBe("provider-unsupported");
        _fixture.Transport.SnapshotHttp().ShouldBe(before);
        var retained = (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(document.NativeContext.ItemKey))!;
        retained.Generation.ShouldBe(0);
        retained.Changes.ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task CommandHumanAndMinimalOutputReportSuccessAndFormattedRefusals()
    {
        var runtime = await SeedAsync();
        var module = Module(runtime);
        foreach (var format in new[] { "human", "minimal" })
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var command = new DraftEditingCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = error },
                () => module, output);
            (await command.OpenAsync(42, "default", format)).ShouldBe(0);
            output.ToString().ShouldStartWith("draft opened\t42\t");
            error.ToString().ShouldBeEmpty();
            output.GetStringBuilder().Clear();
            var opened = await module.OpenAsync(42, "default");
            opened.Error.ShouldBeNull(opened.Error?.Message);
            var request = Request(opened.Document!, [new("System.Title", $"intent-{format}")]);
            using var input = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request,
                TwigJsonContext.Default.DraftStageRequest));
            var stageCommand = new DraftEditingCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = error },
                () => module, output, input);
            (await stageCommand.StageAsync(true, format)).ShouldBe(0);
            output.ToString().ShouldStartWith("draft staged\t42\t");
            error.ToString().ShouldBeEmpty();
            output.GetStringBuilder().Clear();
            var forRetention = await module.OpenAsync(42, "default");
            forRetention.Error.ShouldBeNull(forRetention.Error?.Message);
            using var retentionInput = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(RetainRequest(forRetention.Document!),
                TwigJsonContext.Default.DraftRetainRequest));
            var retainCommand = new DraftEditingCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = error },
                () => runtime.GetRequiredService<RecoverableDraftEditingModule>(), output, retentionInput);
            (await retainCommand.RetainAsync(true, format)).ShouldBe(0);
            output.ToString().ShouldStartWith("draft retained\t");
            error.ToString().ShouldBeEmpty();
            output.GetStringBuilder().Clear();
            (await command.StageAsync(false, format)).ShouldBe(2);
            output.ToString().ShouldBeEmpty();
            error.ToString().ShouldContain("--stdin");
        }
    }

    private async Task<ServiceProvider> SeedAsync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = (await benches.GetByNameAsync("default"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        return runtime;
    }

    private RecoverableDraftEditingModule Module(ServiceProvider runtime, IRecoverableWorkItemDraftStore? drafts = null,
        IWorkItemProvider? provider = null) => new(
        runtime.GetRequiredService<BrowserOperationModule>(),
        new BrowserEditingModule(runtime.GetRequiredService<BrowserOperationModule>(),
            runtime.GetRequiredService<IWorkItemRepository>(), runtime.GetRequiredService<IPendingChangeStore>(),
            new DetailDocumentSource(_layout), runtime.GetRequiredService<IProcessConfigurationProvider>()),
        runtime.GetRequiredService<SqliteCacheStore>(), drafts ?? runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
        runtime.GetRequiredService<IWorkItemRepository>(), runtime.GetRequiredService<IPendingChangeStore>(),
        runtime.GetRequiredService<IPlanOriginProvider>(), provider ?? runtime.GetRequiredService<IWorkItemProvider>(), _rules,
        runtime.GetRequiredService<IDraftProposalCaptureStore>());

    private static DraftStageRequest Request(DraftEditingDocument document, IReadOnlyList<DraftEditingChange> changes)
        => new(1, document.NativeContext, document.Generation, document.BaselineRevision, document.ObservedRevision, changes);

    private static void CaptureEnvelope(string name, DraftEditingResult result)
    {
        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DraftEditingResult);
        if (result.Status is "opened" or "staged")
        {
            result.Capture.ShouldBeNull();
            using var wire = JsonDocument.Parse(json);
            wire.RootElement.TryGetProperty("capture", out _).ShouldBeFalse(
                "The optional RETAIN receipt must not change existing OPEN/STAGE v1 output.");
        }
        var restored = JsonSerializer.Deserialize(json, TwigJsonContext.Default.DraftEditingResult);
        restored.ShouldNotBeNull();
        JsonSerializer.Serialize(restored, TwigJsonContext.Default.DraftEditingResult).ShouldBe(json);
        CaptureJson(name, json);
    }

    private static void CaptureJson(string name, string json)
    {
        var directory = Environment.GetEnvironmentVariable("TWIG_DRAFT_EDITING_TEST_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    private sealed class RuleProvider : IProcessRuleProvider
    {
        internal bool Refuse;
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(string type, CancellationToken ct = default)
            => Refuse ? throw new InvalidOperationException("STAGE must not reacquire rules.")
                : Task.FromResult<IReadOnlyList<ProcessRule>>([]);
    }

    private sealed class LayoutProvider : IFormLayoutProvider
    {
        internal bool Refuse;
        internal bool TitleReadOnly;
        public Task<FormLayoutResult> GetFormLayoutAsync(string type, CancellationToken ct = default)
        {
            if (Refuse) throw new InvalidOperationException("STAGE must not reacquire a served layout.");
            return Task.FromResult<FormLayoutResult>(new FormLayoutResult.Served(new("Fixture.Task", "fixture-process",
            [
                new("fields", "Fields", "custom", true, false,
                [
                    new("column", [new("group", "Group", true, false,
                    [
                        new("System.Title", "Title", "FieldControl", TitleReadOnly, true, false),
                        new("System.AssignedTo", "Assigned", "FieldControl", false, true, false),
                        new("System.State", "State", "FieldControl", false, true, false),
                        new("System.Description", "Description", "HtmlControl", true, true, false),
                    ])]),
                ]),
                new("history", "History", "history", true, false, []),
            ])));
        }
    }

    private sealed class AfterSaveStore(IRecoverableWorkItemDraftStore native, Action afterSave) : IRecoverableWorkItemDraftStore
    {
        public Task<RecoverableWorkItemDraft?> GetAsync(QualifiedWorkItemKey key, CancellationToken ct = default)
            => native.GetAsync(key, ct);
        public async Task<bool> TrySaveAsync(RecoverableWorkItemDraft draft, long? expectedGeneration, CancellationToken ct = default)
        {
            var saved = await native.TrySaveAsync(draft, expectedGeneration, ct);
            if (saved) afterSave();
            return saved;
        }
    }
}
