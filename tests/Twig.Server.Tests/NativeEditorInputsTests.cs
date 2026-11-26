using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;
using Xunit;

namespace Twig.Server.Tests;

public sealed class NativeEditorInputsTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-editor-input-http-" + Guid.NewGuid().ToString("N"));
    private readonly Guid profile = Guid.NewGuid();
    private readonly Clock clock = new();
    private ConnectionBindingService bindings = null!;
    private Gateway gateway = null!;
    private NativePanel panel = null!;
    private NativePanelContext source = null!;
    private ViewSessions sessions = null!;
    private PairResult owner = null!;
    private ViewSession ownerSession = null!;
    private UserBenchStore definitions = null!;
    private ServiceProvider services = null!;
    private CoreWorkspaceHandle handle = null!;
    private ItemKey item = null!;
    private Guid bench;
    private WebApplication? app;
    private HttpClient client = null!;
    private string View => $"/api/v1/views/{owner.ViewId:D}/";
    private string Work => Path.Combine(root, "work");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryRefreshSupportsLateOwnerAndCanvasPrepareWithoutAnImplicitRenewal(bool attachLate)
    {
        await Start(executionV2: true);
        var mirror = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(Path.Combine(Work, ".twig", "core.db"))).Value;
        var before = (await handle.Workspace.SnapshotAsync(default)).Value;
        Assert.True((await mirror.CommitAsync(handle.Source.Connection, before.Version, new(before.Version + 1,
            [], before.Relationships, before.RetainedItems, before.Plans, before.Journal, before.Mappings, before.Benches), default)).IsSuccess);
        var catalog = (await definitions.ReadAsync()).Value;
        var original = Assert.Single(catalog.Benches);
        Assert.True((await definitions.SaveAsync(new(bench, original.Name,
            [new(SelectorKind.Query, query: new(QueryKind.All), connection: handle.Source.Connection)]), original.Revision)).IsSuccess);
        var display = app!.Services.GetRequiredService<ReleasedBenchDisplay>();
        using var output = new StringWriter();
        Assert.Equal(0, await display.RefreshAsync(handle.Workspace, handle.Source, definitions, original.Name, output, default));
        var refreshed = JsonSerializer.Deserialize(output.ToString(), DisplayJson.Default.ReleasedPartitions)!;

        var reader = attachLate ? sessions.ConnectOwner(catalog.InstanceId, null, Guid.NewGuid()) : owner;
        client.DefaultRequestHeaders.Authorization = new("Bearer", reader.ContentCapability);
        var directory = $"/api/v2/views/{reader.ViewId:D}/benches/{bench:D}/partitions";
        var inventory = JsonSerializer.Deserialize(await client.GetStringAsync(directory), DisplayJson.Default.ReleasedPartitions)!;
        Assert.Equal(refreshed.Membership.State, inventory.Membership.State);
        Assert.Equal(refreshed.Membership.Items, inventory.Membership.Items);
        var partition = $"{directory}/{Assert.Single(inventory.Partitions):D}";
        using var page = await client.GetAsync(partition + "/page");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var baseline = Assert.Single(page.Headers.GetValues("X-Twig-Item-Version"));
        var write = Guid.NewGuid();
        await Send(HttpMethod.Put, partition + "/input",
            new PrivateInputRequest(item.NativeId!, NativeProvider.Title, write, 0, "do not send", baseline));
        var prepared = await Send(HttpMethod.Post, partition + "/action",
            new CanvasActionRequest(item.NativeId!, NativeProvider.Title, "prepare", write));
        Assert.Equal("Queued", prepared.GetProperty("status").GetString());
        Assert.Single((await handle.Workspace.SnapshotAsync(default)).Value.Items);
        var after = JsonSerializer.Deserialize(await client.GetStringAsync(directory), DisplayJson.Default.ReleasedPartitions)!;
        Assert.Equal(inventory.Generation, after.Generation);
        Assert.Equal(inventory.Membership.Items, after.Membership.Items);
        Assert.Equal("fixture title", (await handle.Workspace.ObserveItemsAsync([item], default)).Value.Items.Single().Fields[NativeProvider.Title].Text);
        display.Withdraw(handle.Source.Connection, item);
        var denied = JsonSerializer.Deserialize(await client.GetStringAsync(directory), DisplayJson.Default.ReleasedPartitions)!;
        Assert.Equal("Denied", denied.Membership.State);
        Assert.Null(denied.Membership.ExactTotal);
        using var revoked = await client.GetAsync(partition + "/page");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain("fixture title", await revoked.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("observe", false)]
    [InlineData("released", true)]
    [InlineData("native", false)]
    public async Task AppliedPlanAcknowledgementCanBeVerifiedAfterRestartWithoutReplay(string recovery, bool create)
    {
        await Start(planMode: true, executionV2: true);
        using var authority = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "home", "native-databases", "database-input-fixture.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await authority.OpenAsync();
        using var effects = authority.CreateCommand();
        effects.CommandText = "SELECT count(*) FROM native_effects";
        var effectCount = (long)(await effects.ExecuteScalarAsync())!;
        using (var editor = (await NativeItemEditor.OpenAsync(Work, bench, handle.Source, gateway.Configure, default)).Value)
        {
            var form = (await editor.SelectAsync(item)).Value;
            var target = create ? (await editor.CreateSeedAsync(form.Type)).Value : form;
            var fields = FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("Acknowledged Plan effect"));
            if (create) fields = fields.Set(form.StateField, form.Values[form.StateField]);
            Assert.True((await editor.StageAsync(target.Ticket, fields)).IsSuccess);
        }
        var status = (await definitions.ReadPlanStatusAsync(bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(Work, bench, status.Sources, gateway.Configure, default)).Value;
        var scope = Assert.Single(review.Document.Scopes).Scope;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString());
        await db.OpenAsync();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            CREATE TRIGGER park_verification BEFORE UPDATE OF document ON core_snapshots
            WHEN json_extract(NEW.document,'$.Journal[0].Status')={(int)OpStatus.Verified}
            BEGIN SELECT RAISE(ABORT,'park original readback'); END;
            """;
        await cmd.ExecuteNonQueryAsync();
        var applied = Assert.Single((await review.ApplyAsync()).Value.Units);
        Assert.Equal("Applied", applied.State);
        Assert.NotNull(applied.Attempt);
        cmd.CommandText = "DROP TRIGGER park_verification";
        await cmd.ExecuteNonQueryAsync();

        var restarted = (await NativePlanReview.OpenAsync(Work, review.Digest, gateway.Configure, default)).Value;
        var display = app!.Services.GetRequiredService<ReleasedBenchDisplay>();
        if (recovery == "observe")
        {
            var observed = (await restarted.ObserveAsync()).Value;
            Assert.Equal("VerifiedDeliveryPending", observed.Status);
            Assert.Equal(applied with { State = "Verified" }, Assert.Single(observed.Units));
            Assert.False(Assert.Single((await definitions.ReadEditorCapturesAsync(default)).Value).Settled);
        }
        var continued = recovery == "native" ? await restarted.ContinueDeliveryAsync()
            : await display.ContinuePlanAsync(Work, handle.Source, definitions, restarted, default);
        Assert.True(continued.IsSuccess, continued.IsSuccess ? "" : continued.Error.Message);
        Assert.Equal("Complete", continued.Value.Status);
        Assert.Equal(applied with { State = "Delivered" }, Assert.Single(continued.Value.Units));
        Assert.True(Assert.Single((await definitions.ReadEditorCapturesAsync(default)).Value).Settled);
        var scoped = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(scope.DatabasePath, default, scope.StoreInstance)).Value;
        var final = (await scoped.ReadAsync(handle.Source.Connection, default)).Value;
        var row = Assert.Single(final.Journal);
        Assert.Equal(new[] { OpStatus.Applied, OpStatus.Verified }, row.Receipts.Select(receipt => receipt.Status));
        Assert.Equal(applied.Attempt, row.Attempt);
        Assert.Empty(final.RetainedItems);
        var pins = Assert.Single((await definitions.ReadAsync()).Value.Benches).Selectors;
        if (create) Assert.Single(pins, pin => pin.Item == Assert.Single(final.Mappings).Remote);
        Assert.Equal(create ? 2 : 1, pins.Length);
        Assert.Equal(effectCount + 1, await effects.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("plan-scope")]
    [InlineData("plan-receipt")]
    [InlineData("newer-edit")]
    [InlineData("replaced-pins")]
    public async Task VerifiedSeedAndEditDeliverTheirOriginalsAndOneMappedPinAcrossRestart(string cut)
    {
        await Start(planMode: true, executionV2: true);
        using var editor = (await NativeItemEditor.OpenAsync(Work, bench, handle.Source, gateway.Configure, default)).Value;
        var edit = (await editor.SelectAsync(item)).Value;
        Assert.True((await editor.StageAsync(edit.Ticket, FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("edited")))).IsSuccess);
        var seed = (await editor.CreateSeedAsync(edit.Type)).Value;
        Assert.True((await editor.StageAsync(seed.Ticket, FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("created"))
            .Set(edit.StateField, edit.Values[edit.StateField]))).IsSuccess);
        var status = (await definitions.ReadPlanStatusAsync(bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(Work, bench, status.Sources, gateway.Configure, default)).Value;
        var scope = Assert.Single(review.Document.Scopes).Scope;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString());
        await db.OpenAsync();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TRIGGER park_cleanup BEFORE UPDATE OF document ON core_snapshots
            WHEN json_array_length(NEW.document,'$.Drafts')=0 AND json_array_length(OLD.document,'$.Drafts')>0
            BEGIN SELECT RAISE(ABORT,'park verified cleanup'); END;
            """;
        await cmd.ExecuteNonQueryAsync();
        var apply = (await review.ApplyAsync()).Value;
        Assert.Equal("VerifiedDeliveryPending", apply.Status);
        cmd.CommandText = "DROP TRIGGER park_cleanup";
        await cmd.ExecuteNonQueryAsync();
        var scoped = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(scope.DatabasePath, default, scope.StoreInstance)).Value;
        var before = (await scoped.ReadAsync(handle.Source.Connection, default)).Value;
        var mapping = Assert.Single(before.Mappings);
        var display = app!.Services.GetRequiredService<ReleasedBenchDisplay>();
        var replaced = await display.ContinuePlanAsync(Work, handle.Source with { StoreInstance = Guid.NewGuid() }, definitions, review, default);
        Assert.False(replaced.IsSuccess);
        if (cut == "replaced-pins")
        {
            var original = Assert.Single((await definitions.ReadAsync()).Value.Benches);
            Assert.True((await definitions.SaveAsync(new(original.Id, original.Name, []), original.Revision)).IsSuccess);
            var outcome = (await display.ContinuePlanAsync(Work, handle.Source, definitions, review, default)).Value;
            Assert.Equal("VerifiedDeliveryPending", outcome.Status);
            Assert.Contains("Bench", outcome.Error!.Message);
            Assert.Empty(Assert.Single((await definitions.ReadAsync()).Value.Benches).Selectors);
            Assert.All((await definitions.ReadEditorCapturesAsync(default)).Value, c => Assert.False(c.Settled));
            return;
        }
        if (cut == "newer-edit")
        {
            var draft = Assert.Single(before.RetainedItems.OfType<LocalEdits>());
            var newer = draft with { CaptureRevision = Guid.NewGuid() };
            Assert.True((await scoped.CommitAsync(handle.Source.Connection, before.Version, new(before.Version + 1,
                before.Items, before.Relationships, before.RetainedItems.Where(d => d.Key != draft.Key).Append(newer),
                before.Plans, before.Journal, before.Mappings, before.Benches), default)).IsSuccess);
            var partial = (await display.ContinuePlanAsync(Work, handle.Source, definitions, review, default)).Value;
            Assert.Equal("VerifiedDeliveryPending", partial.Status);
            Assert.Contains("draft changed", partial.Error!.Message);
            Assert.Single(partial.Units, u => u.State == "Delivered");
            Assert.Single(partial.Units, u => u.State == "Verified");
            Assert.Equal(newer.CaptureRevision, Assert.IsType<LocalEdits>(Assert.Single(
                (await scoped.ReadAsync(handle.Source.Connection, default)).Value.RetainedItems)).CaptureRevision);
            Assert.Contains(Assert.Single((await definitions.ReadAsync()).Value.Benches).Selectors, s => s.Item == mapping.Remote);
            return;
        }
        var fault = false;
        display.StepBoundary = async (where, _) =>
        {
            if (where == cut && !(await scoped.ReadAsync(handle.Source.Connection, default)).Value.RetainedItems.Any(d => d.Key == mapping.Local))
            {
                fault = true;
                throw new IOException("cut mapped seed delivery");
            }
        };
        var interrupted = await display.ContinuePlanAsync(Work, handle.Source, definitions, review, default);
        Assert.True(fault, interrupted.IsSuccess ? interrupted.Value.Status : interrupted.Error.Message);
        var boot = new ReleasedBenchDisplay(Path.Combine(root, "home", "released-benches.db"));
        await boot.RecoverPlans(Work, definitions, default);
        var finished = await boot.ContinuePlanAsync(Work, handle.Source, definitions, review, default);
        Assert.True(finished.IsSuccess, finished.IsSuccess ? "" : finished.Error.Message);
        Assert.Equal("Complete", finished.Value.Status);
        Assert.All(finished.Value.Units, unit => Assert.Equal("Delivered", unit.State));
        var final = (await scoped.ReadAsync(handle.Source.Connection, default)).Value;
        Assert.Empty(final.RetainedItems);
        Assert.Equal(before.Journal.Select(j => j.Attempt), final.Journal.Select(j => j.Attempt));
        Assert.Equal(mapping, Assert.Single(final.Mappings));
        Assert.Equal(2, (await definitions.ReadAsync()).Value.Benches.Single().Selectors.Length);
        Assert.Single((await definitions.ReadAsync()).Value.Benches.Single().Selectors, s => s.Item == mapping.Remote);
        Assert.Equal("Complete", (await boot.ContinuePlanAsync(Work, handle.Source, definitions, review, default)).Value.Status);
    }

    [Theory]
    [InlineData("plan-manifest")]
    [InlineData("plan-scope")]
    [InlineData("plan-reserved")]
    [InlineData("plan-receipt")]
    [InlineData("plan-definitions")]
    public async Task OriginalConvergedPlanCanSettleAcrossOwnerLossWithoutChangingPinsOrReplaying(string cut)
    {
        await Start(planMode: true, executionV2: true);
        using var editor = (await NativeItemEditor.OpenAsync(Work, bench, handle.Source, gateway.Configure, default)).Value;
        var form = (await editor.SelectAsync(item)).Value;
        Assert.True((await editor.StageAsync(form.Ticket, FieldValues.Empty.Set(NativeProvider.Title, form.Values[NativeProvider.Title]))).IsSuccess);
        var status = (await definitions.ReadPlanStatusAsync(bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(Work, bench, status.Sources, gateway.Configure, default)).Value;
        var retained = (await definitions.RetainPlanReviewAsync(review.Document, default)).Value;
        var entry = Assert.Single(review.Document.Scopes);
        var capture = Assert.Single(entry.Captures);
        Assert.True((await definitions.AdvancePlanReviewAsync(retained, true, 0, 1, default)).IsSuccess);
        Assert.True((await definitions.AdmitPlanCaptureAsync(retained, 0, capture.Id, default)).IsSuccess);
        var scopeStore = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(entry.Scope.DatabasePath, default, entry.Scope.StoreInstance)).Value;
        var initial = (await scopeStore.ReadAsync(handle.Source.Connection, default)).Value.Version;
        var authority = (await NativeAuthority.OpenAsync(Path.Combine(root, "home", "native-databases", "database-input-fixture.db"))).Value;
        using var provider = new NativeProvider(authority, scopeStore);
        var partition = new DisplayPartition(profile, ownerSession.CatalogId, bench, Guid.NewGuid(), null, handle.Source.Connection);
        var display = app!.Services.GetRequiredService<ReleasedBenchDisplay>();
        Assert.True((await display.AcquireAsync(partition, item, provider, default, handle.Source)).IsSuccess);
        var hit = false;
        display.StepBoundary = (point, _) =>
        {
            if (point == cut) { hit = true; throw new IOException("Owner lost at " + point); }
            return Task.CompletedTask;
        };
        try
        {
            var interrupted = await display.ContinuePlanAsync(Work, handle.Source, definitions, review, default);
            Assert.False(interrupted.IsSuccess, interrupted.IsSuccess ? interrupted.Value.Status : interrupted.Error.Message);
        }
        catch (IOException) when (hit) { }
        Assert.True(hit, cut);
        var url = $"/api/v2/views/{owner.ViewId:D}/benches/{bench:D}/partitions/{partition.Id:D}/page";
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(url)).StatusCode);
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        app = null;
        var boot = new ReleasedBenchDisplay(Path.Combine(root, "home", "released-benches.db"));
        await boot.RecoverPlans(Work, definitions, default);
        var grant = await boot.AcquireAsync(partition, item, provider, default, handle.Source);
        Assert.Equal(cut is not ("plan-scope" or "plan-reserved"), grant.IsSuccess);
        var result = await boot.ContinuePlanAsync(Work, handle.Source, definitions, review, default);
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Message);
        Assert.Equal("Complete", result.Value.Status);
        Assert.Equal("Complete", (await boot.ContinuePlanAsync(Work, handle.Source, definitions, review, default)).Value.Status);
        var final = (await scopeStore.ReadAsync(handle.Source.Connection, default)).Value;
        Assert.Equal(initial + 1, final.Version);
        Assert.Empty(final.RetainedItems);
        Assert.True(Assert.Single((await definitions.ReadEditorCapturesAsync(default)).Value).Settled);
        Assert.Equal(item, Assert.Single(Assert.Single((await definitions.ReadAsync()).Value.Benches).Selectors).Item);
        Assert.True((await boot.AcquireAsync(partition, item, provider, default, handle.Source)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedInputPreparesOneOriginalAndKeepsTextThroughPublicationOrDeliberateRereview(bool changeBaseline)
    {
        await Start(executionV2: true);
        var local = Path.Combine(root, "home", "native-databases", "database-input-fixture.db");
        var authority = (await NativeAuthority.OpenAsync(local)).Value;
        using var provider = new NativeProvider(authority, (await Twig.Store.Sqlite.SqliteStore.OpenAsync(
            handle.Document.DatabasePath, default, handle.Document.DatabaseInstance)).Value);
        var scope = new DisplayPartition(profile, ownerSession.CatalogId, bench, Guid.NewGuid(), null, handle.Source.Connection);
        var display = app!.Services.GetRequiredService<ReleasedBenchDisplay>();
        Assert.True((await display.AcquireAsync(scope, item, provider, default, handle.Source)).IsSuccess);
        var prefix = $"/api/v2/views/{owner.ViewId:D}/benches/{bench:D}/partitions/{scope.Id:D}";
        using var page = await client.GetAsync(prefix + "/page");
        var baseline = Assert.Single(page.Headers.GetValues("X-Twig-Item-Version"));
        var write = Guid.NewGuid();
        var body = new PrivateInputRequest(item.NativeId!, NativeProvider.Title, write, 0, "first text", baseline);
        await Send(HttpMethod.Put, prefix + "/input", body);
        var prepare = new CanvasActionRequest(body.Item, body.Field, "prepare", write);
        var original = await Send(HttpMethod.Post, prefix + "/action", prepare);
        original.GetProperty("status").GetString().ShouldBe("Queued");
        var submission = original.GetProperty("matches")[0].GetProperty("original").GetProperty("submission").GetGuid();
        if (changeBaseline)
        {
            using var independent = (await NativeItemEditor.OpenAsync(Work, bench, source.Stamp, gateway.Configure, default)).Value;
            var fresh = (await independent.SelectAsync(item)).Value;
            Assert.True((await independent.SaveAsync(fresh.Ticket,
                FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("independent change")))).Value.Verified);
            Assert.True((await display.AcquireAsync(scope, item, provider, default, handle.Source)).IsSuccess);
            var refused = await Send(HttpMethod.Post, prefix + "/action", prepare with { Mode = "submit" });
            refused.GetProperty("status").GetString().ShouldBe("ReviewRequired");
            Assert.True((await display.AcquireAsync(scope, item, provider, default, handle.Source)).IsSuccess);
            using var nextPage = await client.GetAsync(prefix + "/page");
            var nextBaseline = Assert.Single(nextPage.Headers.GetValues("X-Twig-Item-Version"));
            var kept = await Send(HttpMethod.Post, prefix + "/input/inspect", new PrivateInputRequest(body.Item, body.Field));
            kept.GetProperty("text").GetString().ShouldBe("first text");
            var nextWrite = Guid.NewGuid();
            var next = body with { Baseline = nextBaseline, Expected = 1, Write = nextWrite };
            await Send(HttpMethod.Put, prefix + "/input", next, HttpStatusCode.Conflict);
            await Send(HttpMethod.Put, prefix + "/input", next with { Rereview = true });
            var nextPrepare = prepare with { Write = nextWrite };
            var replacement = await Send(HttpMethod.Post, prefix + "/action", nextPrepare);
            var replacementId = replacement.GetProperty("matches")[0].GetProperty("original").GetProperty("submission").GetGuid();
            replacementId.ShouldNotBe(submission);
            await Send(HttpMethod.Post, prefix + "/action", nextPrepare with { Mode = "submit" }, HttpStatusCode.Conflict);
            Assert.True((await display.AcquireAsync(scope, item, provider, default, handle.Source)).IsSuccess);
            var published = await Send(HttpMethod.Post, prefix + "/action", nextPrepare with { Mode = "observe" });
            published.GetProperty("status").GetString().ShouldBe("Complete");
            var ambiguous = await Send(HttpMethod.Post, prefix + "/action", prepare with { Mode = "find", Write = null });
            ambiguous.GetProperty("ambiguous").GetBoolean().ShouldBeTrue();
            var before = (await provider.ReadAsync(scope.Connection, [item], default, captureOnly: true)).Value.Items.Single();
            before.Fields[NativeProvider.Title].Text.ShouldBe("first text");
            await Send(HttpMethod.Post, prefix + "/action", nextPrepare with { Mode = "observe" });
            var after = (await provider.ReadAsync(scope.Connection, [item], default, captureOnly: true)).Value.Items.Single();
            after.Revision.ShouldBe(before.Revision);
            await Send(HttpMethod.Put, prefix + "/input", next with { Expected = 2, Write = Guid.NewGuid(), Rereview = true },
                HttpStatusCode.Conflict);
            return;
        }
        await Send(HttpMethod.Put, prefix + "/input", body with { Write = Guid.NewGuid(), Expected = 1, Text = "later input" });
        var duplicate = await Send(HttpMethod.Post, prefix + "/action", prepare);
        duplicate.GetRawText().ShouldBe(original.GetRawText());
        var discovery = await Send(HttpMethod.Post, prefix + "/action", prepare with { Mode = "find", Write = null });
        discovery.GetRawText().ShouldBe(original.GetRawText());
        await Send(HttpMethod.Post, prefix + "/action", prepare with { Mode = "submit" }, HttpStatusCode.Conflict);
        Assert.True((await display.AcquireAsync(scope, item, provider, default, handle.Source)).IsSuccess);
        var result = await Send(HttpMethod.Post, prefix + "/action", prepare with { Mode = "observe" });
        result.GetProperty("status").GetString().ShouldBe("ReviewRequired");
        result.GetProperty("matches")[0].GetProperty("original").GetProperty("submission").GetGuid().ShouldBe(submission);
        var observed = (await provider.ReadAsync(scope.Connection, [item], default, captureOnly: true)).Value.Items.Single();
        observed.Fields[NativeProvider.Title].Text.ShouldBe("fixture title");
        var retained = await Send(HttpMethod.Post, prefix + "/input/inspect", new PrivateInputRequest(body.Item, body.Field));
        retained.GetProperty("text").GetString().ShouldBe("later input");
    }

    [Fact]
    public async Task HttpInputsRecoverAfterServerAndViewReplacementAndObserveLostPutWithoutNativeEffects()
    {
        await Start();
        var first = await Open();
        var before = (await handle.Workspace.SnapshotAsync(default)).Value;
        var original = Guid.NewGuid();
        var pending = Values("private incomplete input");
        await Send(HttpMethod.Put, "editor/inputs", new EditorInputWriteBody(first.Ticket, first.Inputs!.Key, 0, original, pending));
        var observed = await Observe(first);
        observed.WriteId.ShouldBe(original);
        observed.Values!.Fields[0].Value.Value.GetString().ShouldBe("private incomplete input");
        var after = (await handle.Workspace.SnapshotAsync(default)).Value;
        after.Version.ShouldBe(before.Version);
        after.RetainedItems.ShouldBe(before.RetainedItems);
        JournalEvidence(after).ShouldBe(JournalEvidence(before));
        (await definitions.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();

        await Restart();
        var reopened = await Open();
        reopened.Ticket.ShouldNotBe(first.Ticket);
        reopened.Inputs!.Key.ShouldBe(first.Inputs.Key);
        reopened.Inputs.Status.ShouldBe("Restored");
        reopened.Inputs.WriteId.ShouldBe(original);
        reopened.Inputs.Values!.Fields[0].Value.Value.GetString().ShouldBe("private incomplete input");
        await Send(HttpMethod.Post, "editor/inputs/observe", new EditorInputObserveBody(first.Ticket, first.Inputs.Key),
            HttpStatusCode.Conflict);
        using var workspace = await client.GetAsync(View + "workspace");
        (await workspace.Content.ReadAsStringAsync()).ShouldNotContain("private incomplete input");
        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/v1/status"));
        status.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())
            .ShouldContain("private-editor-inputs-v1");
    }

    [Fact]
    public async Task ExactAcknowledgedInputsGateNativeSaveAndAreConsumedOnlyOnConfirmedSuccess()
    {
        await Start();
        var form = await Open();
        var fields = Values("acknowledged title").Fields;
        await Send(HttpMethod.Put, "editor/inputs", new EditorInputWriteBody(form.Ticket, form.Inputs!.Key, 0, Guid.NewGuid(), new(fields)));
        var reference = new EditorInputReference(form.Inputs.Key, 1);
        await Send(HttpMethod.Post, "editor/save", new EditorChangeBody(form.Ticket, Values("substituted").Fields, reference),
            HttpStatusCode.Conflict);
        (await Observe(form)).Revision.ShouldBe(1);
        var saved = await Send(HttpMethod.Post, "editor/save", new EditorChangeBody(form.Ticket, fields, reference));
        saved.GetProperty("verified").GetBoolean().ShouldBeTrue();
        saved.GetProperty("draftRetained").GetBoolean().ShouldBeFalse();
        await Send(HttpMethod.Post, "editor/inputs/observe", new EditorInputObserveBody(form.Ticket, form.Inputs.Key),
            HttpStatusCode.Conflict);
        var next = await Open();
        next.Inputs!.Status.ShouldBe("Consumed");
        next.Inputs.Values.ShouldBeNull();
        next.Fields.Single(field => field.Reference == NativeProvider.Title).Value.Value.GetString().ShouldBe("acknowledged title");
        var snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
        snapshot.Journal.ShouldNotBeEmpty();
        (await definitions.ReadEditorCapturesAsync(default)).Value.ShouldAllBe(capture => capture.Settled);
    }

    [Fact]
    public async Task ClosePreservesRawInputsAndDiscardAfterStagePreservesNativeStagedDebt()
    {
        await Start(planMode: true);
        var first = await Open(planMode: true);
        var fields = Values("stage this title").Fields;
        var retained = (await Send(HttpMethod.Put, "editor/inputs",
            new EditorInputWriteBody(first.Ticket, first.Inputs!.Key, first.Inputs.Revision, Guid.NewGuid(), new(fields))))
            .Deserialize(TransportJson.Default.EditorInputRecoveryDto)!;
        await Send(HttpMethod.Post, "editor/close", new EditorCloseBody(first.Ticket));
        var reopened = await Open(planMode: true);
        reopened.Inputs!.Status.ShouldBe("Restored");
        reopened.Inputs.Revision.ShouldBe(retained.Revision);
        var staged = (await Send(HttpMethod.Post, "editor/stage",
            new EditorChangeBody(reopened.Ticket, fields, new(retained.Key, retained.Revision))))
            .Deserialize(TransportJson.Default.EditorFormDto)!;
        staged.Inputs!.Status.ShouldBe("Consumed");
        var newer = (await Send(HttpMethod.Put, "editor/inputs",
            new EditorInputWriteBody(staged.Ticket, staged.Inputs.Key, staged.Inputs.Revision,
                Guid.NewGuid(), Values("unsubmitted after Stage"))))
            .Deserialize(TransportJson.Default.EditorInputRecoveryDto)!;
        var before = (await handle.Workspace.SnapshotAsync(default)).Value;
        var drafts = (await ownerSession.Editor.Native!.ReadPlanDraftsAsync()).Value;
        drafts.ShouldNotBeEmpty();
        var captures = (await definitions.ReadEditorCapturesAsync(default)).Value;
        captures.ShouldContain(capture => !capture.Settled);
        await Send(HttpMethod.Post, "editor/inputs/discard",
            new EditorInputDiscardBody(staged.Ticket, newer.Key, newer.Revision, Guid.NewGuid()));
        await Send(HttpMethod.Post, "editor/close", new EditorCloseBody(staged.Ticket));
        var after = (await handle.Workspace.SnapshotAsync(default)).Value;
        after.Version.ShouldBe(before.Version);
        after.RetainedItems.ShouldBe(before.RetainedItems);
        JournalEvidence(after).ShouldBe(JournalEvidence(before));
        (await definitions.ReadEditorCapturesAsync(default)).Value.Select(value => (value.Id, value.Revision, value.Settled))
            .ShouldBe(captures.Select(value => (value.Id, value.Revision, value.Settled)));
        (await Open(planMode: true)).Inputs!.Status.ShouldBe("Discarded");
        (await ownerSession.Editor.Native!.ReadPlanDraftsAsync()).Value.Select(draft => (draft.Key, draft.Fields[NativeProvider.Title].Text))
            .ShouldBe(drafts.Select(draft => (draft.Key, draft.Fields[NativeProvider.Title].Text)));
    }

    [Fact]
    public async Task FreshNativeBaselineConflictIsDecidedByServerAndDiscardDoesNotDeleteNativeEvidence()
    {
        await Start();
        var form = await Open();
        await Send(HttpMethod.Put, "editor/inputs", new EditorInputWriteBody(form.Ticket, form.Inputs!.Key, 0, Guid.NewGuid(), Values("retained")));
        using (var independent = (await NativeItemEditor.OpenAsync(Work, bench, source.Stamp, gateway.Configure, default)).Value)
        {
            var fresh = (await independent.SelectAsync(item)).Value;
            var saved = await independent.SaveAsync(fresh.Ticket,
                FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("independent native save")));
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
        }
        var next = await Open();
        next.Inputs!.Status.ShouldBe("Conflict");
        next.Inputs.Values.ShouldBeNull();
        var before = (await handle.Workspace.SnapshotAsync(default)).Value;
        var captures = (await definitions.ReadEditorCapturesAsync(default)).Value;
        await Send(HttpMethod.Post, "editor/inputs/discard",
            new EditorInputDiscardBody(next.Ticket, next.Inputs.Key, 1, Guid.NewGuid()));
        await Send(HttpMethod.Post, "editor/close", new EditorCloseBody(next.Ticket));
        var after = (await handle.Workspace.SnapshotAsync(default)).Value;
        after.Version.ShouldBe(before.Version);
        after.RetainedItems.ShouldBe(before.RetainedItems);
        JournalEvidence(after).ShouldBe(JournalEvidence(before));
        (await definitions.ReadEditorCapturesAsync(default)).Value.Select(value => (value.Id, value.Revision, value.Settled))
            .ShouldBe(captures.Select(value => (value.Id, value.Revision, value.Settled)));
        (await Open()).Inputs!.Status.ShouldBe("Discarded");
    }

    [Theory]
    [InlineData("denial")]
    [InlineData("expiry")]
    [InlineData("signout")]
    [InlineData("wrong-owner")]
    public async Task NoInputContentEscapesAfterSourceOrOwnerAuthorityIsLost(string change)
    {
        await Start();
        var form = await Open();
        await Send(HttpMethod.Put, "editor/inputs", new EditorInputWriteBody(form.Ticket, form.Inputs!.Key, 0, Guid.NewGuid(), Values("private secret")));
        if (change == "denial")
        {
            gateway.Denied = true;
            (await panel.CaptureAsync(source, default)).IsSuccess.ShouldBeFalse();
        }
        else if (change == "expiry") clock.Now += TimeSpan.FromHours(25);
        else if (change == "signout") sessions.Revoke(sessions.Content(owner.ViewId, owner.ContentCapability, true));
        else
        {
            var other = sessions.ConnectOwner((await definitions.ReadAsync()).Value.InstanceId, null, Guid.NewGuid());
            client.DefaultRequestHeaders.Authorization = new("Bearer", other.ContentCapability);
        }
        var failure = await Send(HttpMethod.Post, "editor/inputs/observe",
            new EditorInputObserveBody(form.Ticket, form.Inputs.Key), HttpStatusCode.Forbidden);
        failure.GetRawText().ShouldNotContain("private secret");
        var refused = await Send(HttpMethod.Put, "editor/inputs",
            new EditorInputWriteBody(form.Ticket, form.Inputs.Key, 1, Guid.NewGuid(), Values("late")),
            change == "denial" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden);
        refused.GetRawText().ShouldNotContain("private secret");
    }

    private async Task Start(bool planMode = false, bool executionV2 = false)
    {
        Directory.CreateDirectory(Work);
        using (var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = Work, UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "init", "--quiet" } })!)
        {
            await git.WaitForExitAsync();
            git.ExitCode.ShouldBe(0);
        }
        var initialized = await new NativeConnectionSetup(Path.Combine(root, "home"))
            .InitializeFreshAsync(new(Work, new NativeTwigSetupTarget("input-fixture")));
        initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
        bindings = new(Path.Combine(root, "home"));
        gateway = new(bindings);
        var inventory = (await NativeCoreExecution.InventoryAsync(bindings.RegistryPath)).Value;
        (await NativeCoreExecution.ActivateAsync(bindings.RegistryPath, inventory, inventory.Digest,
            completeInventoryConfirmed: true, oldProcessesQuiesced: true)).IsSuccess.ShouldBeTrue();
        var configuration = await TwigConfiguration.LoadSplitAsync(TwigPaths.BuildPaths(Path.Combine(Work, ".twig"), new(), Work));
        var collection = new ServiceCollection();
        gateway.Configure(collection);
        collection.AddCoreWorkspaceServices(configuration, TwigPaths.BuildPaths(Path.Combine(Work, ".twig"), configuration, Work));
        services = collection.BuildServiceProvider();
        handle = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var seed = (await handle.Workspace.StageSeedAsync(NativeProvider.ItemType,
            FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("fixture title"))
                .Set(NativeProvider.StateField, FieldValue.String("Open")), default)).Value;
        var preview = (await handle.Workspace.PreviewDraftAsync(seed.Key, default)).Value;
        (await handle.Workspace.SaveDraftAsync(preview, default)).Value.EffectsVerified.ShouldBeTrue();
        item = (await handle.Workspace.SnapshotAsync(default)).Value.Mappings.Single().Remote;
        definitions = UserBenchStore.ForRegistry(bindings.RegistryPath);
        bench = (await definitions.SaveAsync(new(Guid.NewGuid(), "Fixture",
            [new(SelectorKind.Pin, item, connection: handle.Source.Connection)]), 0)).Value.Id;
        if (planMode)
        {
            var plan = (await definitions.ReadPlanStatusAsync(bench)).Value;
            (await definitions.ChangePlanModeAsync(plan.UserInstance, bench, plan.BenchRevision, plan.Revision, true))
                .IsSuccess.ShouldBeTrue();
            using var prepared = (await NativeItemEditor.OpenAsync(Work, bench, handle.Source, gateway.Configure, default)).Value;
            prepared.PlanMode.ShouldBeTrue();
        }
        await StartApp(executionV2);
    }

    private async Task StartApp(bool executionV2 = false)
    {
        var changes = new CatalogChanges();
        panel = new(definitions, gateway, new(Path.Combine(root, "admissions"), gateway, changes));
        source = (await panel.OpenAsync(Work, default)).Value;
        sessions = new(Path.Combine(root, "profiles"), clock);
        owner = sessions.ConnectOwner((await definitions.ReadAsync()).Value.InstanceId, null, profile);
        var session = ownerSession = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        session.CurrentBenchId = bench;
        session.NativeContext = source;
        session.DisplayContexts[source.Stamp.Connection] = source;
        var boot = Guid.NewGuid();
        app = NativeBenchHost.Build(new(0, executionV2 ? Path.Combine(root, "home") : root, ServerId: Guid.NewGuid(),
            ProcessInstance: boot, ExecutionV2: executionV2), panel, sessions, changes);
        await app.StartAsync();
        client = new() { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        if (executionV2) client.DefaultRequestHeaders.Add("X-Twig-Execution", $"local-execution-v2/{boot:D}");
    }

    private async Task Restart()
    {
        ownerSession.Editor.Invalidate(retire: true);
        client.Dispose();
        await app!.StopAsync();
        await app.DisposeAsync();
        await StartApp();
    }

    private async Task<EditorFormDto> Open(bool planMode = false)
        => (await Send(HttpMethod.Post, "editor", new EditorOpenBody(source.Source.Id,
            ViewMapping.Key(new(source.Source.Id, item)), bench, planMode))).Deserialize(TransportJson.Default.EditorFormDto)!;

    private async Task<EditorInputRecoveryDto> Observe(EditorFormDto form)
        => (await Send(HttpMethod.Post, "editor/inputs/observe", new EditorInputObserveBody(form.Ticket, form.Inputs!.Key)))
            .Deserialize(TransportJson.Default.EditorInputRecoveryDto)!;

    private async Task<JsonElement> Send<T>(HttpMethod method, string path, T body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith('/') ? path : View + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body,
                (JsonTypeInfo<T>)TransportJson.Default.GetTypeInfo(typeof(T))!), Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(expected, json);
        using var result = JsonDocument.Parse(json);
        return result.RootElement.Clone();
    }

    private static EditorInputValues Values(string value)
        => new([new(NativeProvider.Title, new("Text", JsonSerializer.SerializeToElement(value, TransportJson.Default.String), null))]);

    private static IEnumerable<string> JournalEvidence(StoreSnapshot snapshot)
        => snapshot.Journal.Select(row => $"{row.Digest}/{row.Operation}/{row.Attempt}/{row.Status}/"
            + string.Join(";", row.Receipts.Select(receipt => $"{receipt.Digest}/{receipt.Operation}/{receipt.Attempt}/{receipt.Status}/"
                + $"{receipt.PublishedKey}/{receipt.Revision}/{string.Join(",", receipt.Evidence)}/{receipt.Error?.Code}/{receipt.Error?.Message}")));

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        ownerSession?.Editor.Invalidate(retire: true);
        handle?.Dispose();
        services?.Dispose();
        bindings?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway(ConnectionBindingService bindings) : INativeSourceGateway
    {
        internal bool Denied { get; set; }
        internal void Configure(IServiceCollection services) => services.AddSingleton<IConnectionBindingService>(bindings);
        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
            => Denied ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.PolicyViolation, "Fixture denial"))
                : NativeSourceReader.ReadAsync(root, expected, observeSchema, Configure, ct);
        public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
            => Denied ? ReadAsync(root, expected, false, ct) : NativeSourceReader.ReadOnlineAsync(root, expected, Configure, ct);
        public Task<Result<NativeItemEditor>> OpenExistingEditorAsync(string root, Guid benchId, NativeSourceStamp expected, CancellationToken ct)
            => NativeItemEditor.OpenAsync(root, benchId, expected, Configure, ct, prepareScope: false);
    }
}
