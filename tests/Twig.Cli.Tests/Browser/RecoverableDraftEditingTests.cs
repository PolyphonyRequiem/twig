using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;
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
        runtime.GetRequiredService<IPlanOriginProvider>(), provider ?? runtime.GetRequiredService<IWorkItemProvider>(), _rules);

    private static DraftStageRequest Request(DraftEditingDocument document, IReadOnlyList<DraftEditingChange> changes)
        => new(1, document.NativeContext, document.Generation, document.BaselineRevision, document.ObservedRevision, changes);

    private static void CaptureEnvelope(string name, DraftEditingResult result)
    {
        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DraftEditingResult);
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
