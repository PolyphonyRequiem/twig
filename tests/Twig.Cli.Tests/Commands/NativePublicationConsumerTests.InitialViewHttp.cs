using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeInitialViewHttpRestoresWithOneLocalObservationAndNoProviderWork()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        var clock = Stopwatch.StartNew();
        var phases = new ConcurrentQueue<(long Milliseconds, string Phase)>();
        void Record(string phase) => phases.Enqueue((clock.ElapsedMilliseconds, phase));
        var fixture = await CreateHumanHostAsync();
        Record("fixture created");
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new NativeHttpGateway(this);
        gateway.Trace = Record;
        var changes = new CatalogChanges();
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "initial-view-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "initial-view-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var profile = Guid.NewGuid();
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, profile);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.Value!.EndsWith("/initial-view", StringComparison.Ordinal))
            {
                await next(http);
                return;
            }
            Record("initial-view server entered");
            try { await next(http); }
            finally { Record("initial-view server exited"); }
        });
        await app.StartAsync(ct);
        Record("server started");
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method, JsonNode? body = null)
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", human.ContentCapability);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                Record("response buffered");
                var content = await response.Content.ReadAsStringAsync(ct);
                Record("response decoded");
                var parsed = JsonNode.Parse(content)!;
                Record("response parsed");
                return (response.StatusCode, parsed);
            }
            var admitted = await Send("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            admitted.Status.ShouldBe(HttpStatusCode.OK, admitted.Body.ToJsonString());
            (await Send("/selection", HttpMethod.Put, new JsonObject { ["expectedBenchId"] = null, ["benchId"] = fixture.Bench })).Status.ShouldBe(HttpStatusCode.OK);
            var configured = sessions.Content(human.ViewId, human.ContentCapability, true);
            var state = sessions.ReadState(configured);
            sessions.SaveState(configured, new(state.Revision, state.Choices with
            {
                ViewingRoot = fixture.Root, DisplayRoots = [fixture.Root], BenchId = fixture.Bench,
            }));
            canvas = sessions.RegisterNativeCanvas(instance);
            human = sessions.ConnectOwner(instance, canvas.ViewId, profile);
            var before = await Send("/view-state", HttpMethod.Get);
            before.Status.ShouldBe(HttpStatusCode.OK);
            var beforeLocal = gateway.LocalReads;
            var beforeSchema = gateway.SchemaReads;
            var beforeOnline = gateway.OnlineReads;
            var beforeHttp = _transport.TotalRequests;
            beforeHttp.ShouldBeGreaterThan(0, "The fixture must first complete real native schema admission.");
            var beforeCaptures = await store.ReadEditorCapturesAsync(ct);
            beforeCaptures.IsSuccess.ShouldBeTrue();
            HumanPatchAttempts.ShouldBe(0);
            gateway.HoldOnline();
            try
            {
                using var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                ThreadPool.GetAvailableThreads(out var workersBefore, out var ioBefore);
                Record("initial-view client sending");
                var pending = Send("/initial-view", HttpMethod.Post, new JsonObject());
                (HttpStatusCode Status, JsonNode Body) initial;
                try { initial = await pending.WaitAsync(TimeSpan.FromSeconds(2), ct); }
                catch (Exception error)
                {
                    ThreadPool.GetAvailableThreads(out var workersAfter, out var ioAfter);
                    throw new InvalidOperationException(
                        $"Initial-view failed without relaxing its two-second deadline. Task={pending.Status}; "
                        + $"local={gateway.LocalReads - beforeLocal}, schema={gateway.SchemaReads - beforeSchema}, "
                        + $"online={gateway.OnlineReads - beforeOnline}, http={_transport.TotalRequests - beforeHttp}, "
                        + $"PATCH={HumanPatchAttempts}; "
                        + $"process CPU delta={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds}ms; "
                        + $"available workers={workersBefore}->{workersAfter}, IO={ioBefore}->{ioAfter}. "
                        + "Recorder callbacks and response decoding/parsing are included in the original deadline."
                        + Environment.NewLine
                        + string.Join(Environment.NewLine, phases.Select(value => $"{value.Milliseconds}ms: {value.Phase}")),
                        error);
                }
                initial.Status.ShouldBe(HttpStatusCode.OK, initial.Body.ToJsonString());
                var view = initial.Body["initialView"]!["view"]!;
                view["id"]!.GetValue<string>().ShouldBe(fixture.Bench.ToString("D"));
                var source = view["sources"]![0]!;
                source["errorCode"].ShouldBeNull("Previously admitted native cached rows must remain usable.");
                source["items"]!.AsArray().Select(item => item!["locator"]!.GetValue<string>())
                    .Order().ShouldBe(["42", "43"]);
                JsonNode.DeepEquals(initial.Body["viewState"]!["choices"], before.Body["choices"]).ShouldBeTrue(
                    "Initial recovery retains the persisted configured profile choices.");
                gateway.LocalReads.ShouldBe(beforeLocal + 1, "The complete initial request uses exactly one full local observation.");
                gateway.SchemaReads.ShouldBe(beforeSchema, "Initial recovery does not discover schema.");
                gateway.OnlineReads.ShouldBe(beforeOnline, "Initial recovery does not await the held provider boundary.");
                _transport.TotalRequests.ShouldBe(beforeHttp, "The actual native reader makes no provider HTTP requests.");
                var afterCaptures = await store.ReadEditorCapturesAsync(ct);
                afterCaptures.IsSuccess.ShouldBeTrue();
                afterCaptures.Value.Length.ShouldBe(beforeCaptures.Value.Length, "Initial recovery never captures editor intent.");
                HumanPatchAttempts.ShouldBe(0, "Initial recovery never publishes.");
            }
            finally { gateway.ReleaseOnline(); }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
