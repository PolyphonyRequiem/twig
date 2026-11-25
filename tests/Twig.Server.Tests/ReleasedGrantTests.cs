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
    public async Task PrivateInputReceiptsSurviveReplacementAndRequireTheExactItemGrant()
    {
        await using var f = new Fixture();
        await f.Start();
        f.Client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        var provider = new Provider { Read = (_, key) => Task.FromResult(Observed(key, "baseline")) };
        Assert.True((await f.Display.AcquireAsync(f.Scope, f.Key, provider, default,
            new(f.Scope.Connection, Guid.NewGuid(), "actor", "authority"))).IsSuccess);
        var path = f.PathToPage[..^4] + "input";
        var write = Guid.NewGuid();
        static StringContent Body(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
        using var page = await f.Client.GetAsync(f.PathToPage);
        var baseline = Assert.Single(page.Headers.GetValues("X-Twig-Item-Version"));
        var put = $$"""{"item":"7","field":"field","text":"private","expected":0,"write":"{{write}}","baseline":"{{baseline}}"}""";
        using var first = await f.Client.PutAsync(path, Body(put));
        first.EnsureSuccessStatusCode();
        var second = Guid.NewGuid();
        using var replace = await f.Client.PutAsync(path, Body(put.Replace(write.ToString(), second.ToString())
            .Replace("private", "newer").Replace("\"expected\":0", "\"expected\":1")));
        replace.EnsureSuccessStatusCode();
        using var receipt = await f.Client.PostAsync(path + "/inspect", Body($$"""{"item":"7","field":"field","write":"{{write}}"}"""));
        receipt.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("acknowledged").GetInt64());
        Assert.Equal("newer", json.RootElement.GetProperty("text").GetString());
        using var stale = await f.Client.PutAsync(path, Body(put.Replace(write.ToString(), Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        f.Display.Withdraw(f.Scope.Connection, f.Key);
        using var refused = await f.Client.PostAsync(path + "/inspect", Body("""{"item":"7","field":"field"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("newer", await refused.Content.ReadAsStringAsync());
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
