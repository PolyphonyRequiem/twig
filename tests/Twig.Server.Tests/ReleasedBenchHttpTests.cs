using System.Net;
using System.Net.Http.Headers;
using Bench.Application;
using Bench.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Server.Tests.Native;

public sealed class ReleasedBenchHttpTests
{
    [Fact]
    public async Task AuthorizedOriginShowsOnlyLocalIntentAndContinuationsNeverMixBenchGenerations()
    {
        var root = Directory.CreateTempSubdirectory("twig-display-");
        try
        {
            var store = new UserBenchStore(Path.Combine(root.FullName, "benches.db"));
            var id = (await store.ReadAsync()).Value.InstanceId;
            var changes = new CatalogChanges();
            var gateway = new NativeSourceGateway();
            var panel = new NativePanel(store, gateway, new NativeSourceAdapter(root.FullName, gateway, changes));
            var sessions = new ViewSessions(Path.Combine(root.FullName, "views"));
            var profile = Guid.NewGuid();
            var owner = sessions.ConnectOwner(id, null, profile);
            var boot = Guid.NewGuid();
            await using var app = NativeBenchHost.Build(new(0, root.FullName, ServerId: Guid.NewGuid(),
                ProcessInstance: boot, ExecutionV2: true), panel, sessions);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Twig-Execution", $"local-execution-v2/{boot:D}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
            var bench = Guid.NewGuid();
            var partition = Guid.NewGuid();
            var path = $"/api/v2/views/{owner.ViewId:D}/benches/{bench:D}/partitions/{partition:D}/page";
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("RefreshRequired", await response.Content.ReadAsStringAsync());
            var connection = new Connection(new("ado"), "project", "destination", "actor");
            var origin = new NativeSourceStamp(connection, Guid.NewGuid(), "principal", "bound profile");
            var scope = new DisplayPartition(profile, id, bench, partition, origin, connection);
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "7");
            var baseline = new WorkItem(key, "hidden-type", FieldValues.Empty.Set("secret", FieldValue.String("hidden-baseline")),
                new(key, "opaque"));
            var display = app.Services.GetRequiredService<ReleasedBenchDisplay>();
            NativeSourceObservation From(NativeSourceStamp source, params RetainedItem[] rows) => new(source,
                new(1, retainedItems: rows), DateTimeOffset.UtcNow, null, null);
            var edit = new LocalEdits(key, baseline.Type, baseline, FieldValues.Empty.Set("mine", FieldValue.String("owned edit")));
            Assert.Throws<InvalidDataException>(() => display.PublishLocal(scope, From(origin with { StoreInstance = Guid.NewGuid() }, edit), [key]));
            display.PublishLocal(scope, From(origin, edit), [key]);
            using var subscription = await client.GetAsync($"/api/v2/views/{owner.ViewId:D}/authorization",
                HttpCompletionOption.ResponseHeadersRead);
            subscription.EnsureSuccessStatusCode();
            using var events = new StreamReader(await subscription.Content.ReadAsStreamAsync());
            Assert.Equal("event: ready", await events.ReadLineAsync());
            Assert.StartsWith("data: ", await events.ReadLineAsync());
            Assert.Equal("", await events.ReadLineAsync());
            var definitionsPath = Path.Combine(root.FullName, "benches.db");
            File.Move(definitionsPath, definitionsPath + ".held");
            var page = await client.GetStringAsync(path);
            Assert.Contains("owned edit", page);
            Assert.DoesNotContain("hidden", page);
            Assert.False(File.Exists(definitionsPath));
            var fields = FieldValues.Empty.Set("first", FieldValue.String(new string('a', 300000)))
                .Set("second", FieldValue.String(new string('b', 300000)));
            var seed = new Seed(ItemKey.Local(Guid.NewGuid()), "local type", fields);
            display.PublishLocal(scope, From(origin, seed), [seed.Key]);
            using var first = await client.GetAsync(path);
            var bytes = await first.Content.ReadAsByteArrayAsync();
            Assert.InRange(bytes.Length, 300000, 512 * 1024);
            var cursor = Assert.Single(first.Headers.GetValues("X-Twig-Next-Cursor"));
            client.DefaultRequestHeaders.Add("X-Twig-Cursor", cursor);
            using var second = await client.GetAsync(path);
            Assert.Contains("second", await second.Content.ReadAsStringAsync());
            Assert.False(second.Headers.Contains("X-Twig-Next-Cursor"));
            display.PublishLocal(scope with { Id = Guid.NewGuid() }, From(origin), []);
            using var stale = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("ViewChanged", await stale.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Remove("X-Twig-Cursor");
            display.Withhold(scope);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("event: changed", await events.ReadLineAsync(deadline.Token));
            using var detached = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, detached.StatusCode);
            Assert.DoesNotContain("local type", await detached.Content.ReadAsStringAsync());
        }
        finally { root.Delete(true); }
    }
}
