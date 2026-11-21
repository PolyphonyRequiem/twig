using System.Net;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;
using NativeBench = Twig.Core.Bench;

namespace Twig.Daemon.Tests;

public sealed class NativeWorkspaceTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-workspace-" + Guid.NewGuid().ToString("N"));
    private readonly CatalogChanges changes = new();
    private readonly Gateway gateway = new();
    private readonly Clock clock = new();
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private WebApplication? app;
    private UserBenchStore definitions = null!;
    private ViewSessions sessions = null!;
    private PairResult owner = null!;
    private Guid profile;
    private string View => $"/api/v1/views/{owner.ViewId:D}/";

    [Fact]
    public async Task WorkspaceRestoresProfileAndProjectsStampedSourcesAndProtectedDraftsWithoutProviderRefresh()
    {
        await Start();
        var source = await Source("admitted");
        var draft = new Draft(ItemKey.Local(Guid.NewGuid()), "fixture.issue", null, FieldValues.Empty);
        gateway.Snapshots[source.Root] = new(7, drafts: [draft]);
        var bench = await Bench("Selected", source.Stamp.Connection);
        SaveSelection(bench.Id);

        var workspace = await Workspace();
        workspace.GetProperty("viewState").GetProperty("profileId").GetGuid().ShouldBe(profile);
        workspace.GetProperty("viewState").GetProperty("currentBenchId").GetGuid().ShouldBe(bench.Id);
        workspace.GetProperty("session").GetProperty("preferences").GetProperty("benchId").GetGuid().ShouldBe(bench.Id);
        var view = workspace.GetProperty("view");
        view.GetProperty("id").GetGuid().ShouldBe(bench.Id);
        view.GetProperty("revision").GetInt64().ShouldBe(workspace.GetProperty("summary").GetProperty("revision").GetInt64());
        var observed = view.GetProperty("sources")[0];
        observed.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        observed.GetProperty("mode").GetString().ShouldBe("PermittedOfflineCache");
        observed.GetProperty("cacheVersion").GetInt64().ShouldBe(7);
        observed.GetProperty("schemaObservedAt").GetDateTimeOffset().ShouldBe(gateway.SchemaAt);
        observed.GetProperty("schemaFingerprint").GetString().ShouldNotBeNullOrEmpty();
        observed.GetProperty("limitations").EnumerateArray().Select(value => value.GetString())
            .ShouldContain(value => value != null && value.Contains("Offline cached evidence", StringComparison.Ordinal));
        var protectedDraft = observed.GetProperty("items").EnumerateArray().Single();
        protectedDraft.GetProperty("hasDraft").GetBoolean().ShouldBeTrue();
        protectedDraft.GetProperty("benchMember").GetBoolean().ShouldBeFalse();
        workspace.GetProperty("reconcileAfterSeconds").GetInt32().ShouldBe(30);
        gateway.SchemaReads.ShouldBe(1);
        gateway.OnlineReads.ShouldBe(1); // Initial admission only.
        await Workspace();
        gateway.SchemaReads.ShouldBe(1);
        gateway.OnlineReads.ShouldBe(1);
        gateway.Snapshots[source.Root].Drafts.ShouldContain(draft);
        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/v1/status"));
        status.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())
            .ShouldContain("workspace-read-v1");
    }

    [Fact]
    public async Task WorkspaceAndInitialViewPreserveLocalAccountDisplayWithoutProviderAcquisition()
    {
        await Start();
        await Source("account", identity: """["fixture","identity","pat","fixture"]""");
        var workspace = await Workspace();
        var account = workspace.GetProperty("summary").GetProperty("workspaces")[0].GetProperty("account");
        account.GetProperty("name").GetString().ShouldBe("Personal access token");
        account.GetProperty("kind").GetString().ShouldBe("pat");
        using var response = await client.PostAsync(View + "initial-view",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var initial = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        initial.RootElement.GetProperty("initialView").GetProperty("summary").GetProperty("workspaces")[0]
            .GetProperty("account").GetProperty("kind").GetString().ShouldBe("pat");
        gateway.SchemaReads.ShouldBe(1);
        gateway.OnlineReads.ShouldBe(1); // Initial admission only.
    }

    [Fact]
    public async Task DeniedSourceIsExplicitAndWithheldWhileOtherSourcesRemainAdmitted()
    {
        await Start();
        var allowed = await Source("allowed");
        var denied = await Source("denied");
        var bench = await Bench("Partial", allowed.Stamp.Connection, denied.Stamp.Connection);
        SaveSelection(bench.Id);
        await Workspace();
        gateway.Denied.Add(denied.Root);
        var workspace = await Workspace();
        var sources = workspace.GetProperty("view").GetProperty("sources").EnumerateArray().ToArray();
        sources.Single(value => value.GetProperty("sourceId").GetGuid() == allowed.Source.Id)
            .GetProperty("cacheVersion").GetInt64().ShouldBe(1);
        var refusal = sources.Single(value => value.GetProperty("sourceId").GetGuid() == denied.Source.Id);
        refusal.GetProperty("errorCode").GetString().ShouldBe("PolicyViolation");
        refusal.GetProperty("items").GetArrayLength().ShouldBe(0);
        refusal.GetProperty("cacheVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        refusal.GetProperty("connection").GetProperty("identityRef").GetString().ShouldBe(denied.Stamp.Connection.IdentityRef);
        gateway.Denied.Clear();
        // A subsequent local read cannot silently undo a known denial.
        var stillDenied = await Workspace();
        stillDenied.GetProperty("view").GetProperty("sources").EnumerateArray()
            .Single(value => value.GetProperty("sourceId").GetGuid() == denied.Source.Id)
            .GetProperty("errorCode").GetString().ShouldBe("PolicyViolation");
    }

    [Fact]
    public async Task UnadmittedRegisteredConnectionDoesNotTriggerSchemaOrOnlineAcquisition()
    {
        await Start();
        var source = await Source("not-admitted", admit: false);
        var bench = await Bench("Not admitted", source.Stamp.Connection);
        SaveSelection(bench.Id);
        var workspace = await Workspace();
        workspace.GetProperty("view").GetProperty("sources")[0].GetProperty("errorCode").GetString().ShouldBe("Unavailable");
        gateway.SchemaReads.ShouldBe(0);
        gateway.OnlineReads.ShouldBe(0);
    }

    [Fact]
    public async Task ChangedSelectionDuringReadIsRefusedEvenWhenItReturnsToOriginalBench()
    {
        await Start();
        var source = await Source("race");
        var first = await Bench("First", source.Stamp.Connection);
        var second = await Bench("Second", source.Stamp.Connection);
        await Select(first.Id);
        await Workspace();
        var (entered, release) = BlockNextRead();
        var reading = client.GetAsync(View + "workspace");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Select(second.Id, first.Id);
        await Select(first.Id, second.Id);
        release.SetResult();
        using var refused = await reading;
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("ViewStateConflict");
        (await Workspace()).GetProperty("view").GetProperty("id").GetGuid().ShouldBe(first.Id);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("catalogue")]
    [InlineData("profile")]
    [InlineData("local-selection")]
    [InlineData("exclusions")]
    [InlineData("signout")]
    [InlineData("expiry")]
    public async Task ConcurrentLocalOrAuthorityChangesNeverReleaseMixedWorkspace(string change)
    {
        await Start();
        var source = await Source("race");
        var first = await Bench("First", source.Stamp.Connection);
        await Select(first.Id);
        await Workspace();
        var (entered, release) = BlockNextRead();
        var reading = client.GetAsync(View + "workspace");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "snapshot":
                gateway.AfterRead = () => gateway.Snapshots[source.Root] = new(2);
                break;
            case "catalogue":
                (await definitions.SaveAsync(new(first.Id, "Renamed", [], connections: first.Connections), first.Revision))
                    .IsSuccess.ShouldBeTrue();
                break;
            case "profile":
                var other = sessions.ConnectOwner((await definitions.ReadAsync()).Value.InstanceId, null, profile);
                var otherSession = sessions.Content(other.ViewId, other.ContentCapability, true);
                var state = sessions.ReadState(otherSession);
                sessions.SaveState(otherSession, new(state.Revision, state.Choices with { Layout = "tree" }));
                break;
            case "local-selection":
                gateway.AfterRead = () => gateway.SelectedBench = Guid.NewGuid();
                break;
            case "exclusions":
                gateway.AfterRead = () => gateway.Exclusions = [new(SelectorKind.Pin, ItemKey.Local(Guid.NewGuid()))];
                break;
            case "signout":
                sessions.Revoke(sessions.Content(owner.ViewId, owner.ContentCapability, true));
                break;
            case "expiry":
                clock.Now += TimeSpan.FromHours(25);
                break;
        }
        release.SetResult();
        using var refused = await reading;
        refused.StatusCode.ShouldBe(change is "signout" or "expiry" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldNotContain("\"sources\":");
    }

    [Fact]
    public async Task SavedSelectionFromAnotherAttachmentIsAdoptedWithoutResettingExplicitCurrentSelection()
    {
        await Start();
        var first = await Bench("First");
        var second = await Bench("Second");
        await Workspace();
        var other = sessions.ConnectOwner((await definitions.ReadAsync()).Value.InstanceId, null, profile);
        var otherSession = sessions.Content(other.ViewId, other.ContentCapability, true);
        var before = sessions.ReadState(otherSession);
        sessions.SaveState(otherSession, new(before.Revision, before.Choices with { BenchId = second.Id }));
        (await Workspace()).GetProperty("view").GetProperty("id").GetGuid().ShouldBe(second.Id);
        await Select(first.Id, second.Id);
        (await Workspace()).GetProperty("view").GetProperty("id").GetGuid().ShouldBe(first.Id);
        (await Workspace()).GetProperty("view").GetProperty("id").GetGuid().ShouldBe(first.Id);
    }

    [Fact]
    public async Task OwnSavedSelectionAndInitialPayloadUseTheSameWorkspaceProjection()
    {
        await Start();
        var first = await Bench("First");
        var second = await Bench("Second");
        SaveSelection(first.Id);
        (await Workspace()).GetProperty("view").GetProperty("id").GetGuid().ShouldBe(first.Id);
        SaveSelection(second.Id);
        var workspace = await Workspace();
        workspace.GetProperty("view").GetProperty("id").GetGuid().ShouldBe(second.Id);
        using var response = await client.PostAsync(View + "initial-view",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var initial = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payload = initial.RootElement.GetProperty("initialView");
        payload.GetProperty("view").GetRawText().ShouldBe(workspace.GetProperty("view").GetRawText());
        payload.GetProperty("session").GetRawText().ShouldBe(workspace.GetProperty("session").GetRawText());
        payload.GetProperty("summary").GetRawText().ShouldBe(workspace.GetProperty("summary").GetRawText());
    }

    [Fact]
    public async Task DeletedSelectionReturnsCatalogueAndExplicitFailureWithoutInventingAnEmptyBench()
    {
        await Start();
        var bench = await Bench("Deleted");
        SaveSelection(bench.Id);
        await Workspace();
        (await definitions.DeleteAsync(bench.Id, bench.Revision)).IsSuccess.ShouldBeTrue();
        var workspace = await Workspace();
        workspace.GetProperty("view").ValueKind.ShouldBe(JsonValueKind.Null);
        workspace.GetProperty("selectionError").GetProperty("code").GetString().ShouldBe("NotFound");
        workspace.GetProperty("summary").GetProperty("benches").GetArrayLength().ShouldBe(0);
        workspace.GetProperty("session").GetProperty("preferences").GetProperty("benchId").GetGuid().ShouldBe(bench.Id);
    }

    [Fact]
    public async Task RestorationPreservesChosenViewingSourceInsteadOfUsingFirstBenchConnection()
    {
        await Start();
        var first = await Source("first");
        var second = await Source("second");
        var bench = await Bench("Several sources", first.Stamp.Connection, second.Stamp.Connection);
        SaveSelection(bench.Id);
        await Workspace();
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        var state = sessions.ReadState(session);
        sessions.SaveState(session, new(state.Revision, state.Choices with
        { ViewingRoot = second.Root, DisplayRoots = [first.Root, second.Root] }));
        (await Workspace()).GetProperty("summary").GetProperty("contextRoot").GetString().ShouldBe(second.Root);
    }

    [Fact]
    public async Task RetainedExactBindingCannotBeSubstitutedByNewlyAdmittedIdentityAtSameRoot()
    {
        await Start();
        var source = await Source("identity");
        var bench = await Bench("Exact source", source.Stamp.Connection);
        SaveSelection(bench.Id);
        await Workspace();
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        var state = sessions.ReadState(session);
        sessions.SaveState(session, new(state.Revision, state.Choices with { ViewingRoot = source.Root, DisplayRoots = [source.Root] }));
        gateway.Stamps[source.Root] = source.Stamp with { PrincipalId = "different-principal" };
        var panel = new NativePanel(definitions, gateway,
            new NativeSourceAdapter(Path.Combine(root, "admissions"), gateway, changes));
        (await panel.OpenAsync(source.Root, default)).IsSuccess.ShouldBeTrue();
        owner = sessions.ConnectOwner(state.StoreId, null, profile);
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.ContentCapability);
        var before = gateway.SchemaReads;
        using var refused = await client.GetAsync(View + "workspace");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("SourceChanged");
        gateway.SchemaReads.ShouldBe(before);
    }

    [Fact]
    public async Task ExistingOperationReferenceSurvivesReadsAndStillUsesOriginalObservationEndpoint()
    {
        await Start();
        var bench = await Bench("Original operation");
        SaveSelection(bench.Id);
        await Workspace();
        using var started = await client.PostAsync(View + "plan/review",
            new StringContent($$"""{"benchId":"{{bench.Id}}"}""", Encoding.UTF8, "application/json"));
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var operation = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var operationId = operation.RootElement.GetProperty("operationId").GetGuid();
        (await Workspace()).GetProperty("planOperationId").GetGuid().ShouldBe(operationId);
        (await Workspace()).GetProperty("planOperationId").GetGuid().ShouldBe(operationId);
        using var observed = await client.GetAsync(View + "plan/operations/" + operationId);
        observed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("operationId").GetGuid().ShouldBe(operationId);
    }

    [Fact]
    public async Task RepositoryScopeWithholdsUnassignedSourceWithoutRetargetingSavedBench()
    {
        await Start();
        var allowed = await Source("assigned");
        var unassigned = await Source("unassigned");
        var bench = await Bench("Shared scope", allowed.Stamp.Connection, unassigned.Stamp.Connection);
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "init", "--quiet" },
        })!;
        await git.WaitForExitAsync();
        git.ExitCode.ShouldBe(0);
        (await definitions.SaveRepositoryAsync(repository, 0, [allowed.Stamp.Connection], bench.Id)).IsSuccess.ShouldBeTrue();
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        var state = sessions.ReadState(session);
        sessions.SaveState(session, new(state.Revision, state.Choices with { RepositoryRoot = repository, BenchId = bench.Id }));
        var workspace = await Workspace();
        var sources = workspace.GetProperty("view").GetProperty("sources").EnumerateArray().ToArray();
        sources.Single(value => value.GetProperty("sourceId").GetGuid() == allowed.Source.Id)
            .GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        var withheld = sources.Single(value => value.GetProperty("connection").GetProperty("destination").GetString() == "unassigned");
        withheld.GetProperty("errorCode").GetString().ShouldBe("PolicyViolation");
        withheld.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await definitions.ReadAsync()).Value.Benches.Single().Connections
            .ShouldBe([allowed.Stamp.Connection, unassigned.Stamp.Connection]);
    }

    [Fact]
    public async Task WorkspaceRequiresExactOwnerCapabilityAndCannotExtendExpiredAuthority()
    {
        await Start();
        using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
        (await anonymous.GetAsync(View + "workspace")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var other = sessions.ConnectOwner((await definitions.ReadAsync()).Value.InstanceId, null, Guid.NewGuid());
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", other.ContentCapability);
        (await anonymous.GetAsync(View + "workspace")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        clock.Now += TimeSpan.FromHours(25);
        (await client.GetAsync(View + "workspace")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EventReadyHasLiveSubscriptionAndInvalidationsContainNoResourceContent()
    {
        await Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(View + "events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("event: ready");
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("data: {}");
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("");
        changes.Publish(27);
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("event: changed");
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("data: {}");
    }

    private (TaskCompletionSource Entered, TaskCompletionSource Release) BlockNextRead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.NextRead = async () => { entered.SetResult(); await release.Task; };
        return (entered, release);
    }

    private async Task Start()
    {
        Directory.CreateDirectory(root);
        definitions = new(Path.Combine(root, "benches.db"));
        sessions = new(Path.Combine(root, "profiles"), clock);
        var instance = (await definitions.ReadAsync()).Value.InstanceId;
        profile = Guid.NewGuid();
        owner = sessions.ConnectOwner(instance, null, profile);
        app = NativeBenchHost.Build(new(0, root),
            new(definitions, gateway, new(Path.Combine(root, "admissions"), gateway, changes)), sessions, changes);
        await app.StartAsync();
        client.BaseAddress = new(app.Urls.Single());
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
    }

    private async Task<NativePanelContext> Source(string name, bool admit = true, string identity = "fixture-identity")
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, ".twig"));
        var store = (await SqliteStore.CreateAsync(Path.Combine(path, ".twig", "core.db"))).Value;
        var connection = new Connection(new("ado"), "fixture-collection", name, identity);
        (await definitions.View(store, path, store.InstanceId).ReadAsync(connection, default)).IsSuccess.ShouldBeTrue();
        gateway.Stamps[path] = new(connection, store.InstanceId, "fixture-principal", "fixture-authority");
        gateway.Snapshots[path] = new(1);
        var adapter = new NativeSourceAdapter(Path.Combine(root, "admissions"), gateway, changes);
        var panel = new NativePanel(definitions, gateway, adapter);
        if (admit) return (await panel.OpenAsync(path, default)).Value;
        return new(path, gateway.Stamps[path], new(Guid.NewGuid(), name, path,
            NativeSourceAdapter.Binding(gateway.Stamps[path]), true, gateway.SchemaAt));
    }

    private async Task<NativeBench> Bench(string name, params Connection[] connections)
        => (await definitions.SaveAsync(new(Guid.NewGuid(), name, [], connections: connections), 0)).Value;

    private void SaveSelection(Guid bench)
    {
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        var state = sessions.ReadState(session);
        sessions.SaveState(session, new(state.Revision, state.Choices with { BenchId = bench }));
    }

    private async Task Select(Guid next, Guid? previous = null)
    {
        using var response = await client.PutAsync(View + "selection", new StringContent(
            $$"""{"expectedBenchId":{{(previous is null ? "null" : "\"" + previous + "\"")}},"benchId":"{{next}}"}""",
            Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> Workspace()
    {
        using var response = await client.GetAsync(View + "workspace");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway : INativeSourceGateway
    {
        internal Dictionary<string, NativeSourceStamp> Stamps { get; } = [];
        internal Dictionary<string, StoreSnapshot> Snapshots { get; } = [];
        internal HashSet<string> Denied { get; } = [];
        internal DateTimeOffset SchemaAt { get; } = DateTimeOffset.UtcNow.AddMinutes(-5);
        internal int SchemaReads { get; private set; }
        internal int OnlineReads { get; private set; }
        internal Guid? SelectedBench { get; set; }
        internal System.Collections.Immutable.ImmutableArray<Selector> Exclusions { get; set; } = [];
        internal Func<Task>? NextRead;
        internal Action? AfterRead;
        public async Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        {
            if (observeSchema) SchemaReads++;
            if (Interlocked.Exchange(ref NextRead, null) is { } next) await next();
            if (Denied.Contains(root)) return Result<NativeSourceObservation>.Fail(ErrorCode.PolicyViolation, "Fixture source denied.");
            var snapshot = Snapshots[root];
            var selection = SelectedBench;
            var exclusions = Exclusions;
            Interlocked.Exchange(ref AfterRead, null)?.Invoke();
            return Result<NativeSourceObservation>.Ok(new(Stamps[root], snapshot, DateTimeOffset.UtcNow,
                observeSchema ? Result<ProcessSchema>.Ok(new(Stamps[root].Connection, [], [])) : null,
                observeSchema ? SchemaAt : null) { SelectedBenchId = selection, WorkspaceExclusions = exclusions });
        }
        public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        {
            OnlineReads++;
            return ReadAsync(root, expected, false, ct);
        }
    }
}
