using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativeReadCompositionTests
{
    [Fact]
    public Task RedactsDiagnosticsThroughNativeHostAndLoggingFilter()
        => NativePanelTests.DiagnosticsComposition();

    [Fact]
    public Task RetainsSchemaUntilExplicitNativeRecheck()
        => NativePanelTests.SchemaComposition();
}

internal static partial class NativePanelTests
{
    internal static async Task DiagnosticsComposition()
    {
        await using var fixture = new ReadCompositionFixture(process: false);
        await fixture.StartAsync();
        var factory = fixture.App.Services.GetRequiredService<ILoggerFactory>();
        var diagnosticLogger = factory.CreateLogger("Bench.SourceDiagnostics");
        diagnosticLogger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
        diagnosticLogger.IsEnabled(LogLevel.Information).ShouldBeFalse();
        diagnosticLogger.LogInformation("Filtered diagnostic control.");
        factory.CreateLogger("Unrelated.TestCategory").LogWarning("Filtered category control.");
        fixture.Logs.Entries.ShouldBeEmpty();

        var first = await fixture.BenchSource();
        AssertCleanSource(first);
        var keys = ItemKeys(first);
        keys.Length.ShouldBe(3);
        var sourceId = first["sourceId"]!.GetValue<Guid>();
        var retained = JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        retained["schema"]!["gaps"]!.AsArray().Count.ShouldBe(111);
        fixture.Logs.Entries.Count.ShouldBe(1);
        var initial = fixture.Logs.Entries.Single();
        initial.Message.ShouldBe("Native source diagnostics: count=113, truncated=0, errorCode=None.");
        first["mode"]!.GetValue<string>().ShouldBe("PermittedOfflineCache");
        var picker = (await fixture.Ok("GET", "/picker"))["source"]!;
        AssertCleanSource(picker);
        picker["mode"]!.GetValue<string>().ShouldBe("OnlineValidatedCache");
        fixture.Logs.Entries.Count.ShouldBe(2);
        fixture.Logs.Entries.Last().Message.ShouldBe(initial.Message);
        var primedDrafts = await fixture.Ok("GET", "/drafts");
        AssertNoRawDiagnostics(primedDrafts);
        foreach (var source in primedDrafts["sources"]!.AsArray()) AssertCleanSource(source!);
        fixture.Logs.Entries.Count.ShouldBe(2);

        var refresh = await fixture.Ok("POST", "/planning/refresh");
        refresh["metadata"]!["gaps"]!.AsArray().ShouldBeEmpty();
        AssertNoRawDiagnostics(refresh);
        var planning = await fixture.Ok("POST", "/planning/read");
        planning["metadata"]!["gaps"]!.AsArray().ShouldBeEmpty();
        AssertNoRawDiagnostics(planning);
        var sources = await fixture.Ok("POST", "/planning/sources");
        sources["gaps"]!.AsArray().ShouldBeEmpty();
        AssertNoRawDiagnostics(sources);
        AssertNoRawDiagnostics(await fixture.Ok("POST", "/planning/catalogue"));
        var browse = await fixture.Ok("POST", "/planning/browse",
            new JsonObject { ["kind"] = 0, ["id"] = Gateway.QueryId, ["team"] = null });
        AssertNoRawDiagnostics(browse);
        AssertCleanSource(browse["picker"]!["source"]!);
        browse["picker"]!["source"]!["mode"]!.GetValue<string>().ShouldBe("OnlineMetadata");
        fixture.Logs.Entries.Count.ShouldBe(4);
        fixture.Logs.Entries.Last().Message.ShouldBe("Native source diagnostics: count=112, truncated=0, errorCode=None.");
        fixture.Logs.Entries.Count(entry => entry.Properties.GetValueOrDefault("DiagnosticCount") is 111).ShouldBe(1);

        var baseline = fixture.Logs.Entries.Count;
        var schemaReads = fixture.Gateway.SchemaReads;
        for (var poll = 0; poll < 3; poll++)
        {
            AssertCleanSource(await fixture.BenchSource());
            AssertCleanSource((await fixture.Ok("GET", "/picker"))["source"]!);
            var drafts = await fixture.Ok("GET", "/drafts");
            AssertNoRawDiagnostics(drafts);
            foreach (var source in drafts["sources"]!.AsArray()) AssertCleanSource(source!);
        }
        fixture.Gateway.SchemaReads.ShouldBe(schemaReads);
        fixture.Logs.Entries.Count.ShouldBe(baseline,
            "Primed contexts must not re-emit summaries on alternating Bench, picker and drafts polls.");

        var previous = fixture.Logs.Entries.Count;
        fixture.Gateway.DiagnosticRevision++;
        await fixture.Ok("POST", "/context/recheck");
        var changed = await fixture.BenchSource();
        AssertCleanSource(changed);
        ItemKeys(changed).ShouldBe(keys);
        changed["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        fixture.Logs.Entries.Count.ShouldBe(previous + 1);
        fixture.Logs.Entries.Last().Message.ShouldBe(initial.Message);
        await fixture.BenchSource();
        fixture.Logs.Entries.Count.ShouldBe(previous + 1);
        AssertCleanSource((await fixture.Ok("GET", "/picker"))["source"]!);
        fixture.Logs.Entries.Count.ShouldBe(previous + 2);
        fixture.Logs.Entries.Last().Message.ShouldBe(initial.Message);
        await fixture.Ok("GET", "/drafts");
        fixture.Logs.Entries.Count.ShouldBe(previous + 2);

        fixture.Gateway.DiagnosticRevision--;
        await fixture.Ok("POST", "/context/recheck");
        AssertCleanSource(await fixture.BenchSource());
        fixture.Logs.Entries.Count.ShouldBe(previous + 3);
        AssertCleanSource((await fixture.Ok("GET", "/picker"))["source"]!);
        fixture.Logs.Entries.Count.ShouldBe(previous + 4);
        fixture.Logs.Entries.Last().Message.ShouldBe(initial.Message);

        fixture.Gateway.DiagnosticCount = 1000;
        await fixture.Ok("POST", "/context/recheck");
        var oversized = await fixture.BenchSource();
        AssertCleanSource(oversized);
        oversized["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        ItemKeys(oversized).ShouldBe(keys);
        fixture.Logs.Entries.Count.ShouldBe(previous + 5);
        AssertCleanSource((await fixture.Ok("GET", "/picker"))["source"]!);
        fixture.Logs.Entries.Count.ShouldBe(previous + 6);
        fixture.Logs.Entries.Last().Message.ShouldBe("Native source diagnostics: count=128, truncated=874, errorCode=None.");
        foreach (var entry in fixture.Logs.Entries)
        {
            entry.Category.ShouldBe("Bench.SourceDiagnostics");
            entry.Level.ShouldBe(LogLevel.Warning);
            entry.Message.Length.ShouldBeLessThan(300);
            entry.Message.ShouldMatch(@"^Native source diagnostics: count=\d+, truncated=\d+, errorCode=[A-Za-z]+\.$");
            AssertNoRawDiagnostics(JsonValue.Create(entry.Message)!);
            entry.Properties.Keys.Order(StringComparer.Ordinal).ShouldBe(
                new[] { "DiagnosticCount", "ErrorCode", "TruncatedCount", "{OriginalFormat}" }.Order(StringComparer.Ordinal));
            entry.Properties["DiagnosticCount"].ShouldBeOfType<int>();
            entry.Properties["TruncatedCount"].ShouldBeOfType<int>();
            var code = entry.Properties["ErrorCode"].ShouldBeOfType<string>();
            (code == "None" || Enum.GetNames<ErrorCode>().Contains(code, StringComparer.Ordinal)).ShouldBeTrue();
            entry.Exception.ShouldBeNull();
        }
    }

    internal static async Task SchemaComposition()
    {
        await using var fixture = new ReadCompositionFixture(process: true);
        await fixture.StartAsync();
        var before = await fixture.BenchSource();
        var definitions = (await fixture.Ok("GET", "/benches"))["benches"]!.DeepClone();
        var keys = ItemKeys(before);
        keys.Length.ShouldBe(3);
        var sourceId = before["sourceId"]!.GetValue<Guid>();
        before["types"]!.AsArray().Select(type => type!["reference"]!.GetValue<string>()).Order()
            .ShouldBe(new[] { "Nebula.CustomUnit", "Nebula.SignalUnit" });
        AssertKnownTypes(before);
        var schemaReads = fixture.Gateway.SchemaReads;
        schemaReads.ShouldBe(1);
        var retained = await File.ReadAllBytesAsync(fixture.CachePath);

        fixture.Gateway.ProcessChanged = true;
        for (var poll = 0; poll < 3; poll++)
        {
            var stale = await fixture.BenchSource();
            AssertSameSource(stale, before, keys);
            stale["types"]!.ToJsonString().ShouldBe(before["types"]!.ToJsonString());
            UnmatchedTypes(stale).ShouldBe(3);
            stale["items"]!.AsArray().All(item =>
                item!["type"]!.GetValue<string>().StartsWith("Orion.", StringComparison.Ordinal)).ShouldBeTrue();
        }
        fixture.Gateway.SchemaReads.ShouldBe(schemaReads);
        (await File.ReadAllBytesAsync(fixture.CachePath)).ShouldBe(retained);

        fixture.Gateway.SchemaUnavailable = true;
        var failed = await fixture.Send("POST", "/context/recheck");
        failed.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        failed.Body["code"]!.GetValue<string>().ShouldBe("Unavailable");
        fixture.Gateway.SchemaReads.ShouldBe(++schemaReads);
        (await File.ReadAllBytesAsync(fixture.CachePath)).ShouldBe(retained);
        File.ReadAllText(Path.ChangeExtension(fixture.CachePath, ".check"))
            .ShouldContain("\npending\n");
        // Retained schema is evidence, not an offline grant. Do not emulate the browser's intercepted 503.
        fixture.Gateway.SchemaUnavailable = false;
        var blocked = await fixture.BenchSource();
        blocked["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        blocked["errorCode"]!.GetValue<string>().ShouldBe("PolicyViolation");
        blocked["items"]!.AsArray().ShouldBeEmpty();
        fixture.Gateway.SchemaReads.ShouldBe(schemaReads);
        (await File.ReadAllBytesAsync(fixture.CachePath)).ShouldBe(retained);

        await fixture.Ok("POST", "/context/recheck");
        fixture.Gateway.SchemaReads.ShouldBe(++schemaReads);
        var refreshed = await fixture.BenchSource();
        refreshed["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        ItemKeys(refreshed).ShouldBe(keys);
        refreshed["schemaFingerprint"]!.GetValue<string>().ShouldNotBe(before["schemaFingerprint"]!.GetValue<string>());
        refreshed["schemaObservedAt"]!.GetValue<string>().ShouldNotBe(before["schemaObservedAt"]!.GetValue<string>());
        refreshed["types"]!.AsArray().Select(type => type!["reference"]!.GetValue<string>()).Order()
            .ShouldBe(new[] { "Orion.Decision", "Orion.Signal" });
        AssertKnownTypes(refreshed);
        File.ReadAllText(Path.ChangeExtension(fixture.CachePath, ".check")).ShouldContain("\nallowed\n");

        fixture.Gateway.MissingProcessMetadata = true;
        await fixture.Ok("POST", "/context/recheck");
        fixture.Gateway.SchemaReads.ShouldBe(++schemaReads);
        var unknown = await fixture.BenchSource();
        unknown["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        ItemKeys(unknown).ShouldBe(keys);
        UnmatchedTypes(unknown).ShouldBe(1);
        unknown["types"]!.AsArray().Single()!["reference"]!.GetValue<string>().ShouldBe("Orion.Decision");
        unknown["items"]!.AsArray().Count(item => item!["type"]!.GetValue<string>() == "Orion.Signal").ShouldBe(1);

        fixture.Gateway.MissingProcessMetadata = false;
        await fixture.Ok("POST", "/context/recheck");
        fixture.Gateway.SchemaReads.ShouldBe(++schemaReads);
        var recovered = await fixture.BenchSource();
        AssertKnownTypes(recovered);
        recovered["sourceId"]!.GetValue<Guid>().ShouldBe(sourceId);
        ItemKeys(recovered).ShouldBe(keys);
        recovered["schemaFingerprint"]!.GetValue<string>().ShouldBe(refreshed["schemaFingerprint"]!.GetValue<string>());
        (await fixture.Ok("GET", "/benches"))["benches"]!.ToJsonString().ShouldBe(definitions.ToJsonString());
        fixture.Gateway.SchemaReads.ShouldBe(schemaReads);
    }

    private static string[] ItemKeys(JsonNode source) => source["items"]!.AsArray()
        .Select(item => item!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();

    private static int UnmatchedTypes(JsonNode source) => source["items"]!.AsArray().Count(item =>
        !source["types"]!.AsArray().Any(type => type!["reference"]!.GetValue<string>() == item!["type"]!.GetValue<string>()));

    private static void AssertSameSource(JsonNode actual, JsonNode expected, string[] keys)
    {
        actual["sourceId"]!.GetValue<Guid>().ShouldBe(expected["sourceId"]!.GetValue<Guid>());
        ItemKeys(actual).ShouldBe(keys);
        actual["schemaFingerprint"]!.GetValue<string>().ShouldBe(expected["schemaFingerprint"]!.GetValue<string>());
        actual["schemaObservedAt"]!.GetValue<string>().ShouldBe(expected["schemaObservedAt"]!.GetValue<string>());
    }

    private static void AssertKnownTypes(JsonNode source)
    {
        source["errorCode"].ShouldBeNull();
        source["types"]!.AsArray().Count.ShouldBe(2);
        UnmatchedTypes(source).ShouldBe(0);
        foreach (var item in source["items"]!.AsArray())
        {
            var type = source["types"]!.AsArray().Single(type =>
                type!["reference"]!.GetValue<string>() == item!["type"]!.GetValue<string>())!;
            type["presentation"]!["iconSvg"]!.GetValue<string>().ShouldStartWith("<svg ");
            item!["stateField"]!.GetValue<string>().ShouldBe(
                item["type"]!.GetValue<string>().Contains("Signal", StringComparison.Ordinal) ? "Custom.SignalPhase" : "Custom.UnitPhase");
        }
    }

    private static void AssertCleanSource(JsonNode source)
    {
        source["errorCode"].ShouldBeNull();
        source["limitations"]!.AsArray().ShouldBeEmpty();
        source["treeLimitations"]!.AsArray().ShouldBeEmpty();
        AssertNoRawDiagnostics(source);
    }

    private static void AssertNoRawDiagnostics(JsonNode value)
    {
        var json = value.ToJsonString();
        foreach (var marker in new[] { "SYNTHETIC_DIAGNOSTIC_", "SYNTHETIC_SECRET_MUST_NOT_LOG", "SYNTHETIC_ITEM_CONTENT_MUST_NOT_LOG" })
            json.ShouldNotContain(marker);
    }

    private sealed class ReadCompositionFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "twig-read-composition-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(55));
        private readonly UserBenchStore store;
        private readonly Connection connection = new(new("ado"), "11111111-2222-4333-8444-555555555555",
            "66666666-7777-4888-8999-aaaaaaaaaaaa", """["binding","saved-identity","aad","actor"]""");
        private WebApplication? app;
        private HttpClient? client;
        private ViewSessions? sessions;
        private ViewSession? owned;
        private string prefix = "";
        private Guid benchId;
        internal WebApplication App => app ?? throw new InvalidOperationException("The owned host has not started.");
        internal string Root => root;
        internal UserBenchStore Store => store;
        internal Guid BenchId => benchId;
        internal CancellationToken Token => deadline.Token;
        internal Gateway Gateway { get; }
        internal DiagnosticLogCapture Logs { get; } = new();
        internal string CachePath => Directory.GetFiles(Path.Combine(root, "evidence"), "*.json").Single();

        internal ReadCompositionFixture(bool process)
        {
            Directory.CreateDirectory(root);
            store = new(Path.Combine(root, "benches.db"));
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
            Gateway = new(connection, key, ItemKey.Local(Guid.NewGuid()), store) { DiagnosticFixture = true, ProcessFixture = process };
        }

        internal async Task StartAsync()
        {
            await new TwigConfiguration { Organization = "Example organization", Project = "Example project", Provider = "ado" }
                .SaveAsync(Path.Combine(root, "twig.json"));
            var bench = Required(await store.SaveAsync(new(Guid.NewGuid(), "Read composition",
                [new(SelectorKind.Query, query: new(QueryKind.All), connection: connection)]), 0, deadline.Token));
            benchId = bench.Id;
            var shared = Required(await store.ReadAsync(deadline.Token));
            var stamp = Required(await Gateway.ReadAsync(root, null, false, deadline.Token)).Source;
            await ObserveWorkspace(Path.Combine(root, "benches.db"), root, connection, stamp.StoreInstance);
            var changes = new CatalogChanges();
            var panel = new NativePanel(store, Gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), Gateway, changes));
            sessions = new ViewSessions(Path.Combine(root, "presentation"));
            var registered = sessions.RegisterNativeCanvas(shared.InstanceId);
            var owner = sessions.ConnectOwner(shared.InstanceId, registered.ViewId, Guid.NewGuid());
            owned = sessions.Content(owner.ViewId, owner.ContentCapability, true);
            prefix = $"/api/v1/views/{owner.ViewId:D}";
            app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
            app.Services.GetRequiredService<ILoggerFactory>().AddProvider(Logs);
            await app.StartAsync(deadline.Token);
            client = new() { BaseAddress = new(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
            await Ok("PUT", "/context", new JsonObject { ["root"] = root });
            await Ok("PUT", "/population-context", new JsonObject { ["root"] = root });
        }

        internal async Task<JsonNode> BenchSource() => (await Ok("GET", $"/benches/{benchId:D}"))["sources"]!.AsArray().Single()!;

        internal async Task<JsonNode> Ok(string method, string route, JsonNode? body = null)
        {
            var response = await Send(method, route, body);
            response.Status.ShouldBe(HttpStatusCode.OK, route + ": " + response.Body.ToJsonString());
            return response.Body;
        }

        internal async Task<(HttpStatusCode Status, JsonNode Body)> Send(string method, string route, JsonNode? body = null)
        {
            using var request = new HttpRequestMessage(new(method), prefix + route);
            if (method != "GET")
                request.Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await (client ?? throw new InvalidOperationException("No owned client.")).SendAsync(request, deadline.Token);
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token))!);
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            try
            {
                if (sessions is not null && owned is not null) sessions.Revoke(owned);
                if (app is not null)
                {
                    try { await app.StopAsync(CancellationToken.None); }
                    finally { await app.DisposeAsync(); }
                }
            }
            finally { deadline.Dispose(); Directory.Delete(root, recursive: true); }
        }
    }

    private sealed record DiagnosticLog(string Category, LogLevel Level, string Message,
        Dictionary<string, object?> Properties, Exception? Exception);

    private sealed class DiagnosticLogCapture : ILoggerProvider
    {
        internal ConcurrentQueue<DiagnosticLog> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, ConcurrentQueue<DiagnosticLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new(category, logLevel, formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> values
                        ? values.ToDictionary(value => value.Key, value => value.Value) : [], exception));
        }
    }
}
