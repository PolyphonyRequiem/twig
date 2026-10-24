using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class DraftCaptureCommandTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    private readonly LayoutProvider _layout = new();
    private readonly RuleProvider _rules = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task ExplicitInspectionThenNewCompositionCaptureExportsWithZeroHttpAndNoAuthority()
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        var inspection = new StringWriter();
        (await Command(runtime, inspection).ShowAsync(42, "json", document: true)).ShouldBe(0);
        var fresh = await _fixture.CreateRuntimeAsync();
        var http = _fixture.Transport.HttpAttempts;
        var metadata = _fixture.Transport.MetadataAttempts;
        _fixture.Transport.RejectMetadata = true;
        _layout.Refuse = _rules.Refuse = true;
        var output = new StringWriter();
        (await CaptureAsync(fresh, key, draft, capture, output)).ShouldBe(0);
        using var document = JsonDocument.Parse(output.ToString());
        var digest = document.RootElement.GetProperty("digest").GetString()!;
        var journal = (await fresh.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!;
        (await File.ReadAllTextAsync(journal.SourcePath)).ShouldBe(journal.CanonicalJson);
        journal.DraftCapture!.Version.ShouldBe(2);
        journal.Error.ShouldBeNull();
        journal.AuthorizationMode.ShouldBeNull();
        journal.ConfirmedAt.ShouldBeNull();
        journal.State.ShouldBe(PlanOperationState.Planned);
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        _fixture.Transport.MetadataAttempts.ShouldBe(metadata);
        _fixture.Transport.Mutations.ShouldBe(0);
        _layout.Attempts.ShouldBe(1);
        _rules.Attempts.ShouldBe(1);
        var lifecycle = fresh.GetRequiredService<IPlanLifecycleService>();
        var preview = await lifecycle.PreviewAsync(journal.SourcePath);
        preview.CanApply.ShouldBeFalse();
        preview.Issues.Single().Code.ShouldBe("draft-capture-inspection-only");
        var changed = draft.SetField("System.Title", "Original", "later intent");
        (await fresh.GetRequiredService<IRecoverableWorkItemDraftStore>().TrySaveAsync(changed, draft.Generation)).ShouldBeTrue();
        (await lifecycle.PreviewAsync(journal.SourcePath)).Issues.Single().Code.ShouldBe("draft-capture-stale");
        (await lifecycle.StatusAsync(journal.SourcePath))!.Issues.Single().Code.ShouldBe("draft-capture-stale");
        var after = (await fresh.GetRequiredService<IPlanJournalRepository>().GetAsync(digest))!;
        after.LastPreviewedAt.ShouldBe(journal.LastPreviewedAt);
        after.AuthorizationMode.ShouldBeNull();
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        _fixture.Transport.MetadataAttempts.ShouldBe(metadata);
    }

    [HostMigrationFact]
    public async Task FailedExportRemainsPersistedAndExactDigestRecoveryNeverReimports()
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        var directory = Path.Combine(key.CheckoutRoot, ".twig", "proposals");
        await File.WriteAllTextAsync(directory, "deliberately block the export directory");
        var error = new StringWriter();
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), error)).ShouldBe(1);
        error.ToString().ShouldContain("export-incomplete");
        var journals = runtime.GetRequiredService<IPlanJournalRepository>();
        var journal = (await journals.GetUnresolvedAsync()).Single();
        journal.Error.ShouldContain("export-incomplete");
        journal.ConfirmedAt.ShouldBeNull();
        File.Delete(directory);
        var cache = runtime.GetRequiredService<SqliteCacheStore>();
        using (var command = cache.GetConnection().CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER pending.no_reimport BEFORE INSERT ON proposal_journals
                BEGIN SELECT RAISE(ABORT,'recovery must not import'); END;
                """;
            command.ExecuteNonQuery();
        }
        _fixture.Transport.RejectMetadata = true;
        _layout.Refuse = _rules.Refuse = true;
        var http = _fixture.Transport.HttpAttempts;
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), recover: journal.Digest)).ShouldBe(0);
        (await journals.GetAsync(journal.Digest))!.Error.ShouldBeNull();
        (await journals.GetUnresolvedAsync()).Count.ShouldBe(1);
        (await File.ReadAllTextAsync(journal.SourcePath)).ShouldBe(journal.CanonicalJson);
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), recover: journal.Digest)).ShouldBe(0);
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task HumanAndMinimalCaptureReturnTheSameNativeDigest(string format)
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        var output = new StringWriter();
        (await Command(runtime, output).CaptureAsync(42, capture.BenchId, key.CheckoutRoot,
            key.ConnectionId, key.BindingId, capture.IdentityId, draft.Generation, 3, format)).ShouldBe(0);
        var journal = (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).Single();
        output.ToString().ShouldContain(journal.Digest);
        if (format == "minimal") output.ToString().Trim().ShouldBe($"{journal.Digest}\t{journal.SourcePath}");
    }

    [HostMigrationFact]
    public async Task CanceledExportAfterCommittedCaptureKeepsPersistedRecoveryMarker()
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        var cache = runtime.GetRequiredService<SqliteCacheStore>();
        var evidence = new SqliteDraftCaptureEvidenceStore(cache).Read(key);
        var snapshot = (await new SqliteWorkItemRepository(cache, new Twig.Domain.Services.WorkItemMapper()).GetSnapshotByIdAsync(42))!;
        var journal = await new SqliteDraftProposalCaptureStore(cache).CaptureObservedAsync(draft, snapshot,
            evidence.Origin, new() { Organization = NativeBrowserFixture.Organization, Project = NativeBrowserFixture.Project },
            Path.Combine(key.CheckoutRoot, ".twig", "proposals"), SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DraftCommand.ExportAsync(journal, canceled.Token));
        var retained = (await runtime.GetRequiredService<IPlanJournalRepository>().GetAsync(journal.Digest))!;
        retained.Error.ShouldContain("export-incomplete");
        retained.AuthorizationMode.ShouldBeNull();
        File.Exists(journal.SourcePath).ShouldBeFalse();
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), recover: journal.Digest)).ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RecoveryRefusesDifferentExistingExportBytesWithoutOverwriteOrReimport()
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter())).ShouldBe(0);
        var journals = runtime.GetRequiredService<IPlanJournalRepository>();
        var journal = (await journals.GetUnresolvedAsync()).Single();
        await File.WriteAllTextAsync(journal.SourcePath, "different bytes");
        var error = new StringWriter();
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), error, journal.Digest)).ShouldBe(1);
        error.ToString().ShouldContain("not overwritten");
        (await File.ReadAllTextAsync(journal.SourcePath)).ShouldBe("different bytes");
        (await journals.GetUnresolvedAsync()).Count.ShouldBe(1);
        (await journals.GetAsync(journal.Digest))!.Error.ShouldContain("export-incomplete");
    }

    [HostMigrationTheory]
    [InlineData("missing")]
    [InlineData("fallback")]
    [InlineData("locked")]
    [InlineData("empty-served")]
    [InlineData("failed-inspection")]
    [InlineData("generation")]
    [InlineData("identity")]
    [InlineData("checkout")]
    [InlineData("different-bench")]
    public async Task MissingEvidenceOrChangedCallerFenceRefusesBeforeMetadataAndRetainsIntent(string fault)
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        if (fault != "missing")
        {
            if (fault == "fallback") _layout.Unavailable = true;
            if (fault == "locked") _layout.Locked = true;
            if (fault == "empty-served") _layout.Empty = true;
            (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        }
        if (fault == "failed-inspection")
        {
            _rules.Refuse = true;
            (await Command(runtime, new StringWriter(), new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(1);
            Should.Throw<InvalidOperationException>(() => new SqliteDraftCaptureEvidenceStore(
                runtime.GetRequiredService<SqliteCacheStore>()).Read(key));
        }
        var http = _fixture.Transport.HttpAttempts;
        var metadata = _fixture.Transport.MetadataAttempts;
        _fixture.Transport.RejectMetadata = true;
        _layout.Refuse = _rules.Refuse = true;
        var error = new StringWriter();
        var targetBench = capture.BenchId;
        if (fault == "different-bench")
        {
            var benches = runtime.GetRequiredService<IBenchRepository>();
            var other = await benches.CreateAsync("other-member-bench");
            other.ShouldNotBeNull();
            await benches.AddSelectorAsync(other.Id, BenchSelector.ForItem(42));
            targetBench = other.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var exit = await Command(runtime, new StringWriter(), error).CaptureAsync(42, targetBench,
            fault == "checkout" ? key.CheckoutRoot + "-wrong" : key.CheckoutRoot, key.ConnectionId,
            key.BindingId, fault == "identity" ? "wrong" : capture.IdentityId,
            fault == "generation" ? draft.Generation + 1 : draft.Generation, 3, "json");
        exit.ShouldBe(1);
        if (fault == "different-bench")
            error.ToString().ShouldContain("bench-stale: explicit capture Bench differs");
        JsonDocument.Parse(error.ToString()).Dispose();
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().GetAsync(key))!.Changes.ShouldBe(draft.Changes);
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        _fixture.Transport.MetadataAttempts.ShouldBe(metadata);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ChangedInspectionBenchSelectorsMakePreviewAndStatusStaleWithoutHttp()
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        (await Command(runtime, new StringWriter()).ShowAsync(42, "json", document: true)).ShouldBe(0);
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter())).ShouldBe(0);
        var journal = (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).Single();
        await runtime.GetRequiredService<IBenchRepository>()
            .AddSelectorAsync(long.Parse(capture.BenchId, System.Globalization.CultureInfo.InvariantCulture), BenchSelector.ForItem(43));
        var http = _fixture.Transport.HttpAttempts;
        _fixture.Transport.RejectMetadata = true;
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        (await lifecycle.PreviewAsync(journal.SourcePath)).Issues.Single().Message.ShouldContain("bench-stale:");
        (await lifecycle.StatusAsync(journal.SourcePath))!.Issues.Single().Message.ShouldContain("bench-stale:");
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("failed")]
    [InlineData("locked")]
    [InlineData("unavailable")]
    public async Task RepeatedInspectionOnSameCompositionRevokesReceiptAfterNonservedObservation(string outcome)
    {
        var (runtime, key, draft, capture) = await SeedAsync();
        var command = Command(runtime, new StringWriter(), new StringWriter());
        (await command.ShowAsync(42, "json", document: true)).ShouldBe(0);
        new SqliteDraftCaptureEvidenceStore(runtime.GetRequiredService<SqliteCacheStore>()).Read(key).ShouldNotBeNull();
        _layout.Refuse = outcome == "failed";
        _layout.Locked = outcome == "locked";
        _layout.Unavailable = outcome == "unavailable";
        (await command.ShowAsync(42, "json", document: true)).ShouldBe(outcome == "failed" ? 1 : 0);
        _layout.Attempts.ShouldBe(2);
        Should.Throw<InvalidOperationException>(() => new SqliteDraftCaptureEvidenceStore(
            runtime.GetRequiredService<SqliteCacheStore>()).Read(key));
        var http = _fixture.Transport.HttpAttempts;
        (await CaptureAsync(runtime, key, draft, capture, new StringWriter(), new StringWriter())).ShouldBe(1);
        _fixture.Transport.HttpAttempts.ShouldBe(http);
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
    }

    private async Task<(ServiceProvider, QualifiedWorkItemKey, RecoverableWorkItemDraft, BrowserCapture)> SeedAsync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = await benches.GetByNameAsync("default");
        bench.ShouldNotBeNull();
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        var admitted = await runtime.GetRequiredService<BrowserOperationModule>()
            .ExecuteAsync<BrowserCapture>(null, (capture, _) => Task.FromResult(capture));
        admitted.Failure.ShouldBeNull();
        var capture = admitted.Value!;
        var key = new QualifiedWorkItemKey(capture.Origin.WorktreeRoot, capture.Origin.Binding.ConnectionRef, capture.BindingId, 42);
        var draft = RecoverableWorkItemDraft.Create(key, 3).SetField("System.Title", "Original", "Intent");
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().TrySaveAsync(draft, null)).ShouldBeTrue();
        return (runtime, key, draft, capture);
    }

    private DraftCommand Command(ServiceProvider runtime, TextWriter output, TextWriter? error = null) => new(
        runtime.GetRequiredService<CommandContext>() with { Stderr = error }, _fixture.CreateModule(runtime),
        new BrowserEditingModule(runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<IWorkItemRepository>(),
            runtime.GetRequiredService<IPendingChangeStore>(), new DetailDocumentSource(_layout),
            runtime.GetRequiredService<IProcessConfigurationProvider>()),
        runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
        runtime.GetRequiredService<RendererFactory>(), output, runtime.GetRequiredService<SqliteCacheStore>(),
        runtime.GetRequiredService<IPlanOriginProvider>(), _rules);

    private Task<int> CaptureAsync(ServiceProvider runtime, QualifiedWorkItemKey key, RecoverableWorkItemDraft draft,
        BrowserCapture capture, TextWriter output, TextWriter? error = null, string? recover = null)
        => Command(runtime, output, error).CaptureAsync(42, capture.BenchId, key.CheckoutRoot, key.ConnectionId,
            key.BindingId, capture.IdentityId, draft.Generation, 3, "json", recover);

    private sealed class LayoutProvider : IFormLayoutProvider
    {
        internal bool Refuse, Unavailable, Empty, Locked;
        internal int Attempts;
        public Task<FormLayoutResult> GetFormLayoutAsync(string type, CancellationToken ct = default)
        {
            Attempts++;
            if (Refuse) throw new InvalidOperationException("metadata provider must not run during capture");
            FormLayoutResult result = Locked ? new FormLayoutResult.Locked("Fixture.Task")
                : Unavailable ? new FormLayoutResult.Unavailable() : new FormLayoutResult.Served(
                new("Fixture.Task", "fixture-process", [])
                { SystemControls = Empty ? [] : [new("System.Title", "Title", "FieldControl", false, true, false)] });
            return Task.FromResult(result);
        }
    }
    private sealed class RuleProvider : IProcessRuleProvider
    {
        internal bool Refuse;
        internal int Attempts;
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(string type, CancellationToken ct = default)
        {
            Attempts++;
            if (Refuse) throw new InvalidOperationException("rules provider must not run during capture");
            return Task.FromResult<IReadOnlyList<ProcessRule>>([]);
        }
    }
}
