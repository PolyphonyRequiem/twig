using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
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
        var fixture = await CreateHumanHostAsync();
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new NativeHttpGateway(this);
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
        await app.StartAsync(ct);
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
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!);
            }
            var admitted = await Send("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            admitted.Status.ShouldBe(HttpStatusCode.OK, admitted.Body.ToJsonString());
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
            var beforeSelections = gateway.SelectionWrites;
            var beforeHttp = _transport.TotalRequests;
            beforeHttp.ShouldBeGreaterThan(0, "The fixture must first complete real native schema admission.");
            var beforeCaptures = await store.ReadEditorCapturesAsync(ct);
            beforeCaptures.IsSuccess.ShouldBeTrue();
            HumanPatchAttempts.ShouldBe(0);
            gateway.HoldOnline();
            try
            {
                var initial = await Send("/initial-view", HttpMethod.Post, new JsonObject())
                    .WaitAsync(TimeSpan.FromSeconds(2), ct);
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
                gateway.SelectionWrites.ShouldBe(beforeSelections, "Initial recovery never changes native selection.");
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
