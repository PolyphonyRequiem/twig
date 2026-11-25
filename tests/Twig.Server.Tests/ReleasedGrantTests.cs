using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using Bench.Application;
using Bench.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Server.Tests.Native;

public sealed class ReleasedGrantTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly DirectoryInfo Root = Directory.CreateTempSubdirectory("twig-grants-");
        private WebApplication? app;
        internal HttpClient Client = null!;
        internal DisplayPartition Scope = null!;
        internal ItemKey Key => ItemKey.Remote(Scope.Connection.Provider, Scope.Connection.ResourceScope, "7");
        internal string PathToPage = "";
        internal Guid View;
        internal ReleasedBenchDisplay Display => app!.Services.GetRequiredService<ReleasedBenchDisplay>();

        internal async Task Start()
        {
            var store = new UserBenchStore(Path.Combine(Root.FullName, "benches.db"));
            var id = (await store.ReadAsync()).Value.InstanceId;
            var gateway = new NativeSourceGateway();
            var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Root.FullName, gateway, new CatalogChanges()));
            var sessions = new ViewSessions(Path.Combine(Root.FullName, "views"));
            Scope ??= new(Guid.NewGuid(), id, Guid.NewGuid(), Guid.NewGuid(), null,
                new(new("ado"), "project", "org", "actor"));
            var paired = sessions.ConnectOwner(id, null, Scope.Owner);
            View = paired.ViewId;
            var boot = Guid.NewGuid();
            app = NativeBenchHost.Build(new(0, Root.FullName, ServerId: Guid.NewGuid(), ProcessInstance: boot,
                ExecutionV2: true), panel, sessions);
            await app.StartAsync();
            Client = new() { BaseAddress = new(app.Urls.Single()) };
            Client.DefaultRequestHeaders.Add("X-Twig-Execution", $"local-execution-v2/{boot:D}");
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.ContentCapability);
            PathToPage = $"/api/v2/views/{paired.ViewId:D}/benches/{Scope.Bench:D}/partitions/{Scope.Id:D}/page";
        }

        internal async Task Stop()
        {
            Client.Dispose();
            if (app is not null) await app.DisposeAsync();
            app = null;
        }

        public async ValueTask DisposeAsync()
        {
            await Stop();
            Root.Delete(true);
        }
    }

    private sealed class Provider : IProvider
    {
        internal Func<Connection, ItemKey, Task<Result<QueryPage>>> Read = (_, _) => throw new NotImplementedException();
        public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct, bool captureOnly = false)
            => captureOnly ? Read(connection, Assert.Single(keys)) : throw new InvalidOperationException("Must not update provider mirrors.");
        public Task<Result<bool>> ValidateReadAccessAsync(Connection connection, CancellationToken ct) => Task.FromResult(Result<bool>.Ok(true));
        public ProviderCapabilities Capabilities => throw new NotImplementedException();
        public Task<Result<QueryPage>> QueryAsync(Connection c, Query q, string? cursor, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<long>> ImportAsync(Plan plan, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<Receipt>> ApplyAsync(Plan p, int op, Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<Receipt>> VerifyAsync(Plan p, Receipt r, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result<Receipt>> RecoverAsync(Plan p, int op, CancellationToken ct) => throw new NotImplementedException();
    }

    private static Result<QueryPage> Observed(ItemKey key, string value) => Result<QueryPage>.Ok(new(
        [new(key, "provider-type", FieldValues.Empty.Set("field", FieldValue.String(value)), new(key, "opaque"))], [], null, true));

    [Fact]
    public async Task OwnerWorkspaceCommitPublishesOnlyPinnedLocalIntentAndInvalidatesAnInFlightPage()
    {
        await using var f = new Fixture();
        await f.Start();
        var workspace = Directory.CreateDirectory(Path.Combine(f.Root.FullName, "work"));
        var twig = Directory.CreateDirectory(Path.Combine(workspace.FullName, ".twig"));
        var native = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(Path.Combine(twig.FullName, "core.db"))).Value;
        var c = f.Scope.Connection;
        Assert.True((await native.PinWorkspaceAsync(c)).IsSuccess);
        var origin = new NativeSourceStamp(c, native.InstanceId, "principal", "binding");
        var defs = new UserBenchStore(Path.Combine(f.Root.FullName, "benches.db"));
        var key = ItemKey.Local(Guid.NewGuid());
        Assert.True((await defs.SaveAsync(new(f.Scope.Bench, "Pinned", [new(SelectorKind.Pin, key, connection: c)]), 0)).IsSuccess);
        var path = f.PathToPage.Replace(f.Scope.Id.ToString("D"), native.InstanceId.ToString("D"));
        using var producer = f.Display.TrackWorkspace(workspace.FullName, origin, defs);
        var hidden = new Seed(ItemKey.Local(Guid.NewGuid()), "unselected", FieldValues.Empty);
        StoreSnapshot Next(long version, string title) => new(version, retainedItems:
            [new Seed(key, "seed", FieldValues.Empty.Set("title", FieldValue.String(title))), hidden]);
        Assert.True((await native.CommitAsync(c, 0, Next(1, "first"), default)).IsSuccess);
        var first = await f.Client.GetStringAsync(path);
        Assert.Contains("first", first);
        Assert.DoesNotContain("unselected", first);
        var directory = path[..path.LastIndexOf("/partitions/", StringComparison.Ordinal)] + "/partitions";
        Assert.Contains(native.InstanceId.ToString("D"), await f.Client.GetStringAsync(directory));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Display.BeforeEnqueue = ct => { reached.TrySetResult(); return resume.Task.WaitAsync(ct); };
        var racing = f.Client.GetAsync(path);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True((await native.CommitAsync(c, 1, Next(2, "second"), default)).IsSuccess);
        }
        finally { resume.TrySetResult(); }
        using var refused = await racing;
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain("first", await refused.Content.ReadAsStringAsync());
        Assert.Contains("second", await f.Client.GetStringAsync(path));
    }

    [Theory]
    [InlineData("manifest", 0)]
    [InlineData("domain", 1)]
    [InlineData("settled", 1)]
    [InlineData("staged", 1)]
    public async Task RestartSettlesExactStepWithoutReapplyingOrTrustingOldRelease(string boundary, int expected)
    {
        await using var f = new Fixture();
        await f.Start();
        var root = Path.Combine(f.Root.FullName, "work");
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".twig")).FullName, "core.db");
        var native = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(path)).Value;
        var c = f.Scope.Connection;
        Assert.True((await native.PinWorkspaceAsync(c)).IsSuccess);
        var origin = new NativeSourceStamp(c, native.InstanceId, "principal", "binding");
        var defs = new UserBenchStore(Path.Combine(f.Root.FullName, "benches.db"));
        var key = ItemKey.Local(Guid.NewGuid());
        Assert.True((await defs.SaveAsync(new(f.Scope.Bench, "Pinned", [new(SelectorKind.Pin, key, connection: c)]), 0)).IsSuccess);
        f.Display.StepBoundary = (point, _) => point == boundary ? throw new IOException("cut power") : Task.CompletedTask;
        if (boundary == "staged") f.Display.AfterStage = () => throw new IOException("page staged, not released");
        using (f.Display.TrackWorkspace(root, origin, defs))
            Assert.False((await native.CommitAsync(c, 0, new(1, retainedItems: [new Seed(key, "once", FieldValues.Empty)]), default)).IsSuccess);
        await f.Stop();
        await f.Start();
        var page = f.PathToPage.Replace(f.Scope.Id.ToString("D"), native.InstanceId.ToString("D"));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.GetAsync(page)).StatusCode);
        f.Display.RecoverWorkspace(path);
        await f.Display.ProjectWorkspace(root, origin, defs);
        Assert.Equal(expected, (await native.ReadAsync(c, default)).Value.Version);
        Assert.Equal(expected == 1, (await f.Client.GetStringAsync(page)).Contains("once"));
    }

    [Fact]
    public async Task SettlingDisjointSecondCommitCannotClearFirstBenchDependencyAcrossRestart()
    {
        await using var f = new Fixture();
        await f.Start();
        var root = Path.Combine(f.Root.FullName, "work");
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".twig")).FullName, "core.db");
        var native = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(path)).Value;
        var c = f.Scope.Connection;
        Assert.True((await native.PinWorkspaceAsync(c)).IsSuccess);
        var origin = new NativeSourceStamp(c, native.InstanceId, "principal", "binding");
        var defs = new UserBenchStore(Path.Combine(f.Root.FullName, "benches.db"));
        var a = new Seed(ItemKey.Local(Guid.NewGuid()), "one", FieldValues.Empty);
        var b = new Seed(ItemKey.Local(Guid.NewGuid()), "two", FieldValues.Empty);
        Assert.True((await defs.SaveAsync(new(f.Scope.Bench, "Shared", [new(SelectorKind.Pin, a.Key, connection: c),
                new(SelectorKind.Pin, b.Key, connection: c)]), 0)).IsSuccess);
        var page = f.PathToPage.Replace(f.Scope.Id.ToString("D"), native.InstanceId.ToString("D"));
        using (f.Display.TrackWorkspace(root, origin, defs))
        {
            f.Display.StepBoundary = (phase, version) => phase == "domain" && version == 1
                ? throw new IOException("A lost settlement acknowledgement") : Task.CompletedTask;
            Assert.False((await native.CommitAsync(c, 0, new(1, retainedItems: [a]), default)).IsSuccess);
            Assert.True((await native.CommitAsync(c, 1, new(2, retainedItems: [a, b]), default)).IsSuccess);
            Assert.Equal(HttpStatusCode.Conflict, (await f.Client.GetAsync(page)).StatusCode);
        }
        await f.Stop();
        await f.Start();
        await f.Display.ProjectWorkspace(root, origin, defs);
        page = f.PathToPage.Replace(f.Scope.Id.ToString("D"), native.InstanceId.ToString("D"));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.GetAsync(page)).StatusCode);
        f.Display.RecoverWorkspace(path);
        await f.Display.ProjectWorkspace(root, origin, defs);
        var clean = await f.Client.GetStringAsync(page);
        Assert.Contains("one", clean);
        Assert.Contains("two", clean);
        Assert.Equal(2, (await native.ReadAsync(c, default)).Value.Version);
    }
    [Fact]
    public async Task ExactItemDenialWinsWhileItsProjectStillAllowsReadsAndRestartRestoresNoGrant()
    {
        await using var f = new Fixture();
        await f.Start();
        var provider = new Provider
        {
            Read = (connection, key) =>
            {
                Assert.Equal(f.Scope.Connection, connection);
                Assert.Equal(f.Key, key);
                return Task.FromResult(Observed(key, "protected-content"));
            }
        };
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        using var granted = await f.Client.GetAsync(f.PathToPage);
        Assert.Contains("protected-content", await granted.Content.ReadAsStringAsync());
        Assert.InRange(long.Parse(granted.Headers.GetValues("X-Twig-Remaining-Ms").Single()), 1, 60_000);
        provider.Read = (_, _) => Task.FromResult(Result<QueryPage>.Fail(ErrorCode.PolicyViolation, "item denied"));
        Assert.False((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        Assert.True((await provider.ValidateReadAccessAsync(f.Scope.Connection, default)).Value);
        using var denied = await f.Client.GetAsync(f.PathToPage);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("protected-content", await denied.Content.ReadAsStringAsync());
        await f.Stop();
        await f.Start();
        using var restarted = await f.Client.GetAsync(f.PathToPage);
        Assert.False(restarted.IsSuccessStatusCode);
        Assert.DoesNotContain("protected-content", await restarted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithdrawalBetweenReadAndEnqueueSendsOnlyRefusal()
    {
        await using var f = new Fixture();
        await f.Start();
        var provider = new Provider { Read = (_, key) => Task.FromResult(Observed(key, "revocable-field")) };
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Display.BeforeEnqueue = ct => { waiting.SetResult(); return resume.Task.WaitAsync(ct); };
        var request = f.Client.GetAsync(f.PathToPage);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Display.Withdraw(f.Scope.Connection, null);
        }
        finally { resume.TrySetResult(); }
        using var refused = await request;
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("revocable-field", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PendingDomainStepDoesNotWithdrawAnUnrelatedPinnedBenchOfTheSameOrigin()
    {
        await using var f = new Fixture();
        await f.Start();
        var root = Path.Combine(f.Root.FullName, "work");
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".twig")).FullName, "core.db");
        var native = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(path)).Value;
        var c = f.Scope.Connection;
        Assert.True((await native.PinWorkspaceAsync(c)).IsSuccess);
        var origin = new NativeSourceStamp(c, native.InstanceId, "principal", "binding");
        var defs = new UserBenchStore(Path.Combine(f.Root.FullName, "benches.db"));
        var a = new Seed(ItemKey.Local(Guid.NewGuid()), "one", FieldValues.Empty);
        var b = new Seed(ItemKey.Local(Guid.NewGuid()), "two", FieldValues.Empty);
        var other = Guid.NewGuid();
        Assert.True((await defs.SaveAsync(new(f.Scope.Bench, "A", [new(SelectorKind.Pin, a.Key, connection: c)]), 0)).IsSuccess);
        Assert.True((await defs.SaveAsync(new(other, "B", [new(SelectorKind.Pin, b.Key, connection: c)]), 0)).IsSuccess);
        using var producer = f.Display.TrackWorkspace(root, origin, defs);
        Assert.True((await native.CommitAsync(c, 0, new(1, retainedItems: [a, b]), default)).IsSuccess);
        var page = f.PathToPage.Replace(f.Scope.Id.ToString("D"), native.InstanceId.ToString("D"));
        f.Display.StepBoundary = (phase, _) => phase == "domain" ? throw new IOException("pending settlement") : Task.CompletedTask;
        Assert.False((await native.CommitAsync(c, 1, new(2, retainedItems: [b]), default)).IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.GetAsync(page)).StatusCode);
        Assert.Contains("two", await f.Client.GetStringAsync(page.Replace(f.Scope.Bench.ToString("D"), other.ToString("D"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationRetryDiscardsAllEarlierPagesAndStopsAfterSecondChange(bool churn)
    {
        await using var f = new Fixture();
        await f.Start();
        var version = 0;
        var provider = new Provider
        {
            Read = (_, key) => Task.FromResult(Result<QueryPage>.Ok(new(
            [new(key, $"v{version}", FieldValues.Empty.Set("f1", FieldValue.String(new string('a', 300_000)))
                    .Set("f2", FieldValue.String(new string('b', 300_000))), new(key, "opaque"))], [], null, true)))
        };
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        var passes = 0;
        f.Display.BeforeEnqueue = async ct =>
        {
            if (++passes % 2 == 0 && (churn || version == 0))
            {
                version++;
                Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, ct)).IsSuccess);
            }
        };
        var result = await ReleasedPageReader.ReadAsync(f.Client, f.View, f.Scope.Bench, f.Scope.Id, default);
        Assert.Equal(4, passes);
        Assert.Equal(churn ? "ViewChanged" : "Complete", result.Code);
        if (churn) Assert.Empty(result.Pages);
        else
        {
            Assert.Equal(2, result.Pages.Count);
            Assert.All(result.Pages, p => Assert.Contains("\"v1\"", System.Text.Encoding.UTF8.GetString(p.Body)));
        }
    }

    [Fact]
    public async Task WithdrawalPersistenceFailureClosesEveryPartitionEvenAfterTheStoreReturns()
    {
        await using var f = new Fixture();
        await f.Start();
        var provider = new Provider { Read = (_, key) => Task.FromResult(Observed(key, "protected-content")) };
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        var db = Path.Combine(f.Root.FullName, "released-benches.db");
        File.Move(db, db + ".held");
        Directory.CreateDirectory(db);
        try
        {
            var other = ItemKey.Remote(f.Scope.Connection.Provider, f.Scope.Connection.ResourceScope, "other");
            Assert.Equal("AuthorityUnavailable", Assert.Throws<HttpFailure>(() => f.Display.Withdraw(f.Scope.Connection, other)).Code);
        }
        finally { Directory.Delete(db); File.Move(db + ".held", db); }
        using var unavailablePage = await f.Client.GetAsync(f.PathToPage);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailablePage.StatusCode);
        Assert.DoesNotContain("protected-content", await unavailablePage.Content.ReadAsStringAsync());
        await f.Stop();
        await f.Start();
        using var afterRestart = await f.Client.GetAsync(f.PathToPage);
        Assert.False(afterRestart.IsSuccessStatusCode);
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default)).IsSuccess);
        Assert.Contains("protected-content", await f.Client.GetStringAsync(f.PathToPage));
    }

    [Theory]
    [InlineData("withdrawal")]
    [InlineData("save")]
    [InlineData("acquisition")]
    public async Task LateSuccessfulAcquisitionCannotUndoANewerDecision(string decision)
    {
        await using var f = new Fixture();
        await f.Start();
        var delayed = new TaskCompletionSource<Result<QueryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = f.Display.AcquireAsync(f.Scope, f.Key, new Provider { Read = (_, _) => delayed.Task }, default);
        var fresh = new Provider { Read = (_, key) => Task.FromResult(Observed(key, "new-data")) };
        if (decision == "withdrawal") f.Display.Withdraw(f.Scope.Connection, f.Key);
        else if (decision == "save") f.Display.InvalidateItem(f.Scope.Connection, f.Key);
        else Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, fresh, default)).IsSuccess);
        delayed.SetResult(Observed(f.Key, "old-data"));
        Assert.False((await slow).IsSuccess);
        using var result = await f.Client.GetAsync(f.PathToPage);
        Assert.Equal(decision == "acquisition", result.IsSuccessStatusCode);
        Assert.DoesNotContain("old-data", await result.Content.ReadAsStringAsync());
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, fresh, default)).IsSuccess);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(-Seconds);
    }

    [Fact]
    public async Task RemovingAPinWhileItsExactProviderReadIsPendingCannotPublishOldMembership()
    {
        await using var f = new Fixture();
        await f.Start();
        var defs = new UserBenchStore(Path.Combine(f.Root.FullName, "benches.db"));
        var source = new NativeSourceStamp(f.Scope.Connection, Guid.NewGuid(), "principal", "authority");
        var definition = (await defs.SaveAsync(new(f.Scope.Bench, "Scoped", [
            new(SelectorKind.Pin, f.Key, connection: source.Connection)]), 0)).Value;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new TaskCompletionSource<Result<QueryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = f.Display.Observe(f.Root.FullName, source, defs, f.Key, () =>
        {
            paused.SetResult();
            return remote.Task;
        }, true, default);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True((await defs.DeleteAsync(definition.Id, definition.Revision)).IsSuccess);
        remote.SetResult(Observed(f.Key, "stale member"));
        Assert.False((await result).IsSuccess);
        var directory = f.PathToPage[..f.PathToPage.LastIndexOf("/partitions/", StringComparison.Ordinal)] + "/partitions";
        using var reply = await f.Client.GetAsync(directory);
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
    }

    [Fact]
    public async Task PendingExactReadAndBackwardWallClockNeverExtendPriorGrant()
    {
        var root = Directory.CreateTempSubdirectory("twig-grant-expiry-");
        try
        {
            var clock = new Clock();
            var display = new ReleasedBenchDisplay(Path.Combine(root.FullName, "released.db"), clock);
            var sessions = new ViewSessions(root.FullName, clock);
            var scope = new DisplayPartition(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
                new(new("ado"), "scope", "destination", "actor"));
            var key = ItemKey.Remote(scope.Connection.Provider, scope.Connection.ResourceScope, "7");
            var pair = sessions.ConnectOwner(scope.Store, null, scope.Owner);
            var provider = new Provider { Read = (_, item) => Task.FromResult(Observed(item, "protected")) };
            Assert.True((await display.AcquireAsync(scope, key, provider, default)).IsSuccess);
            Task Read(Func<ReleasedBenchDisplay.Frame, Task> send) => display.Read(sessions, pair.ViewId, pair.ContentCapability,
                scope.Bench, scope.Id, null, null, send, default);
            clock.Seconds = 59;
            var pending = new TaskCompletionSource<Result<QueryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Read = (_, _) => pending.Task;
            var renewal = display.AcquireAsync(scope, key, provider, default);
            await Read(frame => { Assert.Equal(1000, frame.RemainingMilliseconds); return Task.CompletedTask; });
            clock.Seconds = 60;
            var denied = await Assert.ThrowsAsync<HttpFailure>(() => Read(_ => throw new InvalidOperationException("Expired bytes.")));
            Assert.Equal("GrantRequired", denied.Code);
            clock.Seconds = 120;
            pending.SetResult(Observed(key, "too late"));
            Assert.False((await renewal).IsSuccess);
        }
        finally { root.Delete(true); }
    }
}
