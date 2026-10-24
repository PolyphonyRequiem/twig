using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class DraftCommandTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task DocumentCorrelatesNativeIdentityRevisionsValuesAndAdvisoryCapability()
    {
        var runtime = await SeedAsync();
        var key = await KeyAsync(runtime);
        var output = new StringWriter();
        (await Command(runtime, output).ShowAsync(42, "json", document: true)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        var identity = root.GetProperty("itemKey");
        identity.GetProperty("checkoutRoot").GetString().ShouldBe(key.CheckoutRoot);
        identity.GetProperty("connectionId").GetString().ShouldBe(key.ConnectionId);
        identity.GetProperty("bindingId").GetString().ShouldBe(key.BindingId);
        identity.GetProperty("workItemId").GetInt32().ShouldBe(42);
        root.GetProperty("draftDocumentVersion").GetInt32().ShouldBe(1);
        root.GetProperty("generation").GetInt64().ShouldBe(1);
        foreach (var name in new[] { "baselineRevision", "observedRevision", "currentRevision" })
            root.GetProperty(name).GetInt32().ShouldBe(3);
        root.GetProperty("draftStatus").GetString().ShouldBe("staged");
        root.GetProperty("status").GetString().ShouldBe("current");
        root.GetProperty("reconciliationState").GetString().ShouldBe("current");
        root.GetProperty("freshness").GetString().ShouldBe("cached");
        root.GetProperty("observationCompleteness").GetString().ShouldBe("complete-for-draft-effects");
        var field = root.GetProperty("fields")[0];
        field.GetProperty("baselineValue").GetString().ShouldBe("Original");
        field.GetProperty("observedValue").GetString().ShouldBe("Original");
        field.GetProperty("proposedValue").GetString().ShouldBe("Intent");
        field.GetProperty("observedValueAvailable").GetBoolean().ShouldBeTrue();
        field.GetProperty("nativePersistable").GetBoolean().ShouldBeTrue();
        field.GetProperty("nativeCapabilitySource").GetString().ShouldBe("native-edit-sink");
        field.GetProperty("eligibilityReason").ValueKind.ShouldBe(JsonValueKind.Null);
        var eligibility = root.GetProperty("eligibility");
        eligibility.GetProperty("rules").GetString().ShouldBe("not-applicable");
        eligibility.GetProperty("authorized").GetBoolean().ShouldBeFalse();
        eligibility.GetProperty("inspectionOnly").GetBoolean().ShouldBeTrue();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task InspectionPreservesEveryUnrelatedPendingCellAndCreatesNoJournalOrAuthorization()
    {
        var runtime = await SeedAsync();
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(42, "field", "System.Description", "remote", "local");
        await pending.AddChangeAsync(42, "note", null, null, "pending note");
        await pending.AddChangeAsync(43, "field", "System.Title", "other", "unrelated");
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var before = SnapshotRecords(store);
        before.Length.ShouldBe(3, "all three unrelated pending records must exist before inspection");
        var drafts = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var key = await KeyAsync(runtime);
        var draftBefore = await drafts.GetAsync(key);
        draftBefore.ShouldNotBeNull();
        var output = new StringWriter();
        (await Command(runtime, output).ShowAsync(42, "json", document: true)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        root.GetProperty("status").GetString().ShouldBe("blocked-local-work");
        root.GetProperty("localPendingWork").GetBoolean().ShouldBeTrue();
        root.GetProperty("observationCompleteness").GetString().ShouldBe("blocked-by-local-work");
        root.GetProperty("fields")[0].GetProperty("observedValue").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("fields")[0].GetProperty("observedValueAvailable").GetBoolean().ShouldBeFalse();
        root.GetProperty("eligibility").GetProperty("authorized").GetBoolean().ShouldBeFalse();
        root.GetProperty("eligibility").GetProperty("inspectionOnly").GetBoolean().ShouldBeTrue();
        SnapshotRecords(store).ShouldBe(before);
        var after = await drafts.GetAsync(key);
        after.ShouldNotBeNull();
        after.Generation.ShouldBe(draftBefore.Generation);
        after.Changes.ShouldBe(draftBefore.Changes);
        after.Conflicts.ShouldBe(draftBefore.Conflicts);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RevisionDriftWritesOnlyObservationMetadata_AndRetainsBaselineIntentAndLocalStatus()
    {
        var runtime = await SeedAsync();
        var item = NativeBrowserFixture.Item(42, "Server");
        item.MarkSynced(4);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
        var before = SnapshotRecords(runtime.GetRequiredService<SqliteCacheStore>());
        var output = new StringWriter();
        (await Command(runtime, output).ShowAsync(42, "json", document: true)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        root.GetProperty("status").GetString().ShouldBe("conflicted");
        root.GetProperty("draftStatus").GetString().ShouldBe("staged");
        root.GetProperty("baselineRevision").GetInt32().ShouldBe(3);
        root.GetProperty("observedRevision").GetInt32().ShouldBe(4);
        root.GetProperty("currentRevision").GetInt32().ShouldBe(4);
        root.GetProperty("generation").GetInt64().ShouldBe(2);
        root.GetProperty("conflicts")[0].GetProperty("observedValue").GetString().ShouldBe("Server");
        var retained = await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().GetAsync(await KeyAsync(runtime));
        retained.ShouldNotBeNull();
        retained.BaselineRevision.ShouldBe(3);
        retained.ObservedRevision.ShouldBe(4);
        retained.Changes.ShouldBe([new DraftFieldChange("System.Title", "Original", "Intent")]);
        SnapshotRecords(runtime.GetRequiredService<SqliteCacheStore>()).ShouldBe(before);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task MissingDraftRefusesInJsonAndNeverCreatesOne()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var output = new StringWriter();
        var error = new StringWriter();
        var before = SnapshotRecords(runtime.GetRequiredService<SqliteCacheStore>());
        (await Command(runtime, output, error).ShowAsync(42, "json", document: true)).ShouldBe(1);
        output.ToString().ShouldBeEmpty();
        using var json = JsonDocument.Parse(error.ToString());
        json.RootElement.GetProperty("error").GetString().ShouldBe("No recoverable draft exists for work item #42.");
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().GetAsync(await KeyAsync(runtime))).ShouldBeNull();
        SnapshotRecords(runtime.GetRequiredService<SqliteCacheStore>()).ShouldBe(before);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task UnavailableFieldRetainsNullAndEmptyValuesWithoutClaimingCapability()
    {
        var runtime = await SeedAsync("Custom.NotCarried", null, string.Empty);
        var output = new StringWriter();
        (await Command(runtime, output).ShowAsync(42, "json", document: true)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        root.GetProperty("status").GetString().ShouldBe("field-unavailable");
        root.GetProperty("changes")[0].GetProperty("baselineValue").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("changes")[0].GetProperty("proposedValue").GetString().ShouldBe(string.Empty);
        root.GetProperty("fields")[0].GetProperty("nativePersistable").GetBoolean().ShouldBeFalse();
        root.GetProperty("fields")[0].GetProperty("observedValueAvailable").GetBoolean().ShouldBeFalse();
    }

    [HostMigrationFact]
    public async Task MissingProcessMetadataReportsUnknownRatherThanInventingStateEligibility()
    {
        var runtime = await SeedAsync("System.State", "To Do", "Doing");
        var process = Substitute.For<IProcessConfigurationProvider>();
        process.GetConfiguration().Returns(_ => throw new InvalidOperationException("Process metadata unavailable."));
        var operations = runtime.GetRequiredService<BrowserOperationModule>();
        var editing = new BrowserEditingModule(operations, runtime.GetRequiredService<IWorkItemRepository>(),
            runtime.GetRequiredService<IPendingChangeStore>(), runtime.GetRequiredService<DetailDocumentSource>(), process);
        var output = new StringWriter();
        var command = new DraftCommand(runtime.GetRequiredService<CommandContext>(), _fixture.CreateModule(runtime),
            editing, operations, runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
            runtime.GetRequiredService<RendererFactory>(), output);

        (await command.ShowAsync(42, "json", document: true)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("status").GetString().ShouldBe("rules-unknown");
        json.RootElement.GetProperty("eligibility").GetProperty("rules").GetString().ShouldBe("unknown");
        json.RootElement.GetProperty("eligibility").GetProperty("authorized").GetBoolean().ShouldBeFalse();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ObservationCasRefusesConcurrentDraftWithoutRetryOrIntentLoss()
    {
        var runtime = await SeedAsync();
        var item = NativeBrowserFixture.Item(42, "Server");
        item.MarkSynced(4);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
        var real = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var racing = Substitute.For<IRecoverableWorkItemDraftStore>();
        racing.GetAsync(Arg.Any<QualifiedWorkItemKey>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetAsync(call.ArgAt<QualifiedWorkItemKey>(0), call.ArgAt<CancellationToken>(1)));
        racing.TrySaveAsync(Arg.Any<RecoverableWorkItemDraft>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var proposed = call.ArgAt<RecoverableWorkItemDraft>(0);
                var expected = call.ArgAt<long?>(1);
                var token = call.ArgAt<CancellationToken>(2);
                var concurrent = new RecoverableWorkItemDraft(proposed.ItemKey, 3, 3, proposed.Generation,
                    [new DraftFieldChange("System.Title", "Original", "Concurrent intent")], []);
                (await real.TrySaveAsync(concurrent, expected, token)).ShouldBeTrue();
                return await real.TrySaveAsync(proposed, expected, token);
            });
        var error = new StringWriter();
        var output = new StringWriter();
        (await Command(runtime, output, error, racing).ShowAsync(42, "json", document: true)).ShouldBe(1);
        error.ToString().ShouldContain("No retry was attempted");
        output.ToString().ShouldBeEmpty();
        await racing.Received(1).TrySaveAsync(Arg.Any<RecoverableWorkItemDraft>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        var saved = await real.GetAsync(await KeyAsync(runtime));
        saved.ShouldNotBeNull();
        saved.Changes[0].ProposedValue.ShouldBe("Concurrent intent");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task HumanAndMinimalOutputInspectSameExistingDraft_AndDocumentRequiresJson()
    {
        var runtime = await SeedAsync();
        var human = new StringWriter();
        (await Command(runtime, human).ShowAsync(42)).ShouldBe(0);
        human.ToString().ShouldContain("Intent");
        human.ToString().ShouldContain("never authorizes");
        var minimal = new StringWriter();
        (await Command(runtime, minimal).ShowAsync(42, "minimal")).ShouldBe(0);
        minimal.ToString().Trim().ShouldBe("42\t1\t3\t3\t3\tstaged\t1");
        var error = new StringWriter();
        (await Command(runtime, new StringWriter(), error).ShowAsync(42, "human", document: true)).ShouldBe(2);
        error.ToString().ShouldContain("--document requires JSON");
    }

    private async Task<ServiceProvider> SeedAsync(string field = "System.Title", string? baseline = "Original", string? proposed = "Intent")
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var draft = RecoverableWorkItemDraft.Create(await KeyAsync(runtime), 3).SetField(field, baseline, proposed);
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>().TrySaveAsync(draft, null)).ShouldBeTrue();
        return runtime;
    }

    private async Task<QualifiedWorkItemKey> KeyAsync(ServiceProvider runtime)
    {
        var read = await _fixture.CreateModule(runtime).ReadDetailAsync(new(42, Ansi: false, Document: true));
        read.Failure.ShouldBeNull();
        var key = read.Observation!.Document!.ItemKey;
        return new(key.CheckoutRoot, key.ConnectionId, key.BindingId, key.WorkItemId);
    }

    private DraftCommand Command(ServiceProvider runtime, TextWriter output, TextWriter? error = null,
        IRecoverableWorkItemDraftStore? store = null) => new(
            runtime.GetRequiredService<CommandContext>() with { Stderr = error },
            _fixture.CreateModule(runtime), runtime.GetRequiredService<BrowserEditingModule>(),
            runtime.GetRequiredService<BrowserOperationModule>(),
            store ?? runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
            runtime.GetRequiredService<RendererFactory>(), output);

    private static string[] SnapshotRecords(SqliteCacheStore store)
    {
        var rows = new List<string>();
        foreach (var table in new[] { "pending_changes", "proposal_journals", "proposal_operations", "proposal_receipts" })
        {
            using var command = store.GetConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM pending.{table} ORDER BY 1;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var cells = new List<string> { table };
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                    cells.Add(value is null ? "null;" : $"{value.Length}:{value};");
                }
                rows.Add(string.Join('|', cells));
            }
        }
        return rows.ToArray();
    }
}
