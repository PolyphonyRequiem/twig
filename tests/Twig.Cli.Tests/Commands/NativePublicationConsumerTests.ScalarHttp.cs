using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    // AdoEditorTests.cs, Canvas blob 50f1725ea608afb476dc0532851493410df814f7.
    [Theory]
    [InlineData("save")]
    [InlineData("policy-relevant")]
    [InlineData("policy-unrelated")]
    [InlineData("cached-guidance")]
    [InlineData("relationship-debt")]
    public async Task NativeAdoScalarHttpUsesQualifiedNativeAuthority(string scenario)
    {
        var clock = Stopwatch.StartNew();
        void Phase(string phase) => _agentHttpOutput?.WriteLine(
            $"ADO scalar {scenario} {clock.Elapsed.TotalMilliseconds:F0}ms: {phase}; providerRequests={_transport.TotalRequests}, patches={HumanPatchAttempts}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = deadline.Token;
        Phase("initializing fixture");
        var fixture = await CreateHumanHostAsync().WaitAsync(ct);
        Phase("fixture ready");
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        if (scenario.StartsWith("policy-", StringComparison.Ordinal))
        {
            _transport.IncludeIncompleteOtherType = true;
            if (scenario == "policy-relevant")
            {
                _transport.OtherTypeItem = 43;
                _transport.SetNativeRelationship(42, 43, "Native.DependencyForward");
            }
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "twig.policy.json"),
                """{"version":1,"rules":[{"id":"acyclic","kind":"dependency-integrity","enforcement":"refuse","maxNodes":20}]}""", ct);
        }
        if (scenario == "relationship-debt")
        {
            var opened = await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct);
            opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
            using var editor = opened.Value;
            var target = ItemKey.Remote(fixture.Stamp.Connection.Provider, fixture.Stamp.Connection.ResourceScope, "42");
            var endpoint = ItemKey.Remote(fixture.Stamp.Connection.Provider, fixture.Stamp.Connection.ResourceScope, "43");
            var pair = await editor.SelectRelationshipsAsync(target, endpoint, ct);
            pair.IsSuccess.ShouldBeTrue(pair.IsSuccess ? "" : pair.Error.Message);
            var choice = pair.Value.Choices.First(value => !value.Remove);
            var staged = await editor.StageRelationshipsAsync(pair.Value.Fields.Ticket, FieldValues.Empty, [choice], ct);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            staged.Value.Intent.ShouldBe([choice], "The counterfactual needs genuinely owned native intent.");
            staged.Value.Incident.ShouldBeEmpty("Observed presence must not masquerade as staged debt.");
        }
        HumanPatchAttempts.ShouldBe(0);
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new NativeHttpGateway(this);
        var changes = new CatalogChanges();
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "scalar-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "scalar-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, Guid.NewGuid());
        var owned = sessions.Content(human.ViewId, human.ContentCapability, true);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        await app.StartAsync(ct);
        Phase("host listening");
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method,
                JsonNode? body = null, string? capability = null)
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability ?? human.ContentCapability);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                Phase($"request {method} {path}");
                var requestClock = Stopwatch.StartNew();
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                Phase($"response {(int)response.StatusCode} {method} {path}");
                if (requestClock.Elapsed > TimeSpan.FromSeconds(10))
                    Phase($"TIMING WARNING: {method} {path} exceeded 10s request target ({requestClock.ElapsedMilliseconds}ms)");
                text.ShouldNotBeNullOrWhiteSpace(path);
                return (response.StatusCode, JsonNode.Parse(text)!);
            }
            async Task<JsonNode> Ok(string path, HttpMethod method, JsonNode? body = null)
            {
                var response = await Send(path, method, body);
                response.Status.ShouldBe(HttpStatusCode.OK, path + ": " + response.Body.ToJsonString());
                return response.Body;
            }
            async Task<int> Captures()
            {
                var result = await store.ReadEditorCapturesAsync(ct);
                result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
                return result.Value.Length;
            }
            static JsonObject Field(string reference, string kind, string value) => new()
            {
                ["reference"] = reference,
                ["value"] = new JsonObject { ["kind"] = kind, ["value"] = value, ["dataType"] = null },
            };
            static JsonObject Change(JsonNode form, params JsonNode[] fields) => new()
            {
                ["ticket"] = form["ticket"]!.DeepClone(), ["fields"] = new JsonArray(fields),
            };
            await Ok("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            await Ok("/selection", HttpMethod.Put, new JsonObject { ["expectedBenchId"] = null, ["benchId"] = fixture.Bench });
            var view = await Ok($"/benches/{fixture.Bench:D}", HttpMethod.Get);
            var source = view["sources"]!.AsArray().Single()!;
            source["provider"]!.GetValue<string>().ShouldBe("ado");
            source["errorCode"].ShouldBeNull();
            var rows = source["items"]!.AsArray();
            rows.Select(row => row!["locator"]!.GetValue<string>()).Order().ShouldBe(["42", "43"]);
            var item = rows.Single(row => row!["locator"]!.GetValue<string>() == "42")!;
            var open = new JsonObject
            {
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["itemId"] = item["id"]!.DeepClone(),
                ["benchId"] = fixture.Bench.ToString("D"),
            };
            var beforeGuards = _transport.TotalRequests;
            var beforeCaptures = await Captures();
            (await Send("/editor", HttpMethod.Post, open, canvas.PresentationCapability))
                .Status.ShouldBe(HttpStatusCode.Forbidden);
            (await Send("/editor/save", HttpMethod.Post, new JsonObject
            {
                ["ticket"] = Guid.NewGuid().ToString("D"), ["fields"] = new JsonArray(),
            }, canvas.PresentationCapability)).Status.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Remove("X-Twig-Bench");
            (await Send("/editor", HttpMethod.Post, open)).Status.ShouldBe(HttpStatusCode.Forbidden,
                "The owner capability cannot bypass CSRF transport.");
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            _transport.TotalRequests.ShouldBe(beforeGuards, "Capability/CSRF denial precedes provider acquisition.");
            (await Captures()).ShouldBe(beforeCaptures);
            HumanPatchAttempts.ShouldBe(0);

            var online = gateway.OnlineReads;
            var http = _transport.TotalRequests;
            var itemReads = _transport.ItemReadIds.Count;
            var editorMetadata = 0;
            if (scenario == "cached-guidance")
                _transport.BeforeProjectMetadata = () => { editorMetadata++; return Task.CompletedTask; };
            var form = await Ok("/editor", HttpMethod.Post, open);
            _transport.BeforeProjectMetadata = null;
            form["ticket"]!.GetValue<Guid>().ShouldNotBe(Guid.Empty);
            if (scenario == "cached-guidance")
            {
                gateway.OnlineReads.ShouldBe(online);
                online.ShouldBe(0, "Opening from cached guidance never performs a preliminary online source read.");
                var freshHttp = _transport.TotalRequests - http;
                freshHttp.ShouldBeGreaterThan(0, "Native metadata and item admission still use fresh provider transport.");
                editorMetadata.ShouldBeGreaterThan(0, "Count the actual project metadata transport boundary.");
                _transport.ItemReadIds.Skip(itemReads).ShouldContain(42,
                    "The cached target must also be freshly read from the actual item transport.");
                HumanPatchAttempts.ShouldBe(0);
                var endpoint = rows.Single(row => row!["locator"]!.GetValue<string>() == "43")!;
                var pair = await Ok("/editor/relationships", HttpMethod.Post, new JsonObject
                {
                    ["ticket"] = form["ticket"]!.DeepClone(), ["endpointId"] = endpoint["id"]!.DeepClone(),
                });
                var captures = await Captures();
                _transport.SetItem(43, _transport.TitleOf(43), _transport.RevisionOf(43) + 1, Actor);
                var intent = pair["choices"]!.AsArray().First(value => !value!["remove"]!.GetValue<bool>())!;
                var stale = await Send("/editor/relationships/stage", HttpMethod.Post, new JsonObject
                {
                    ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(), ["fields"] = new JsonArray(),
                    ["intent"] = new JsonArray(intent.DeepClone()),
                });
                stale.Status.ShouldBe(HttpStatusCode.Conflict);
                (await Captures()).ShouldBe(captures, "A stale endpoint cannot replace or stage a capture.");
                HumanPatchAttempts.ShouldBe(0);
                var missing = open.DeepClone();
                missing["itemId"] = new string('0', 64);
                (await Send("/editor", HttpMethod.Post, missing)).Status.ShouldBe(HttpStatusCode.NotFound);
                HumanPatchAttempts.ShouldBe(0);
                _transport.SetItem(42, _transport.TitleOf(42), _transport.RevisionOf(42), Sibling);
                (await Send("/editor", HttpMethod.Post, open)).Status.ShouldBe(HttpStatusCode.NotFound,
                    "A cached key cannot release a form when the actual remote item is unreadable.");
                var picker = await Ok("/picker", HttpMethod.Get);
                picker["source"]!["items"]!.AsArray().Any(row =>
                    row!["id"]!.GetValue<string>() == item["id"]!.GetValue<string>()).ShouldBeFalse(
                    "After the provider refuses an exact item, the historical picker must withhold it.");
                HumanPatchAttempts.ShouldBe(0);
                HumanActorDenied = true;
                var denied = await Send("/editor", HttpMethod.Post, open);
                (denied.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict).ShouldBeTrue(denied.Body.ToJsonString());
                denied.Body["fields"].ShouldBeNull();
                var retained = await Ok("/benches", HttpMethod.Get);
                retained["contextRoot"]!.GetValue<string>().ShouldBe(fixture.Root,
                    "The actual HTTP summary must retain the selected native context, not implicitly switch it.");
                (await Captures()).ShouldBe(captures);
                HumanPatchAttempts.ShouldBe(0);
                _agentHttpOutput?.WriteLine($"SCALAR cached-guidance: preliminaryOnline={gateway.OnlineReads - online}; " +
                    $"freshEditorHttp={freshHttp}; freshMetadata={editorMetadata}; captures={captures}; " +
                    "staleStageCaptureDelta=0; providerPatchAttempts=0");
            }
            else if (scenario.StartsWith("policy-", StringComparison.Ordinal))
            {
                var incomplete = source["types"]!.AsArray().Single(type =>
                    type!["reference"]!.GetValue<string>() == "Fixture.Other")!;
                incomplete["gaps"]!.AsArray().ShouldNotBeEmpty("Visible incomplete metadata must survive the wire.");
                var saved = await Send("/editor/save", HttpMethod.Post,
                    Change(form, Field("System.Title", "Text", "Scoped private native edit")));
                var observed = await ReadHumanHostAsync(fixture.Root, fixture.Stamp, false, ct);
                observed.IsSuccess.ShouldBeTrue(observed.IsSuccess ? "" : observed.Error.Message);
                var snapshot = observed.Value.Snapshot;
                if (scenario == "policy-relevant")
                {
                    saved.Status.ShouldBe(HttpStatusCode.ServiceUnavailable, saved.Body.ToJsonString());
                    HumanPatchAttempts.ShouldBe(0);
                    snapshot.Journal.ShouldBeEmpty("An incomplete actual endpoint refuses before a journal attempt.");
                }
                else
                {
                    saved.Status.ShouldBe(HttpStatusCode.OK, saved.Body.ToJsonString());
                    saved.Body["verified"]!.GetValue<bool>().ShouldBeTrue();
                    saved.Body["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                    HumanPatchAttempts.ShouldBe(1);
                    snapshot.Drafts.ShouldBeEmpty();
                    snapshot.Journal.Single().Status.ShouldBe(OpStatus.Verified);
                    var plan = snapshot.Plans.Single();
                    plan.SchemaFingerprint.ShouldBe(source["schemaFingerprint"]!.GetValue<string>());
                    plan.PolicyRevision.ShouldNotBeNullOrWhiteSpace();
                    _transport.TitleOf(42).ShouldBe("Scoped private native edit");
                }
            }
            else if (scenario == "relationship-debt")
            {
                form["fields"]!.AsArray().ShouldNotBeEmpty();
                form["fields"]!.AsArray().ShouldAllBe(field => field!["readOnly"]!.GetValue<bool>());
                var refused = await Send("/editor/evaluate", HttpMethod.Post, Change(form));
                refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.Body.ToJsonString());
                refused.Body["code"]!.GetValue<string>().ShouldBe("Unsupported");
                form["relationshipsReadOnly"]!.GetValue<bool>().ShouldBeTrue();
                form["relationshipIntent"]!.AsArray().Count.ShouldBe(1,
                    "Scalar withholding must explicitly disclose the retained native relationship debt.");
                HumanPatchAttempts.ShouldBe(0);
            }
            else
            {
                scenario.ShouldBe("save");
                form["binding"]!["planMode"]!.GetValue<bool>().ShouldBeFalse();
                var stateField = form["stateField"]!.GetValue<string>();
                stateField.ShouldBe("System.State", "The wire carries the native fixture's discovered state field.");
                form["fields"]!.AsArray().Single(field => field!["reference"]!.GetValue<string>() == "System.Description")!
                    ["kind"]!.GetValue<string>().ShouldBe("Html");
                var saved = await Ok("/editor/save", HttpMethod.Post, Change(form,
                    Field("System.Title", "Text", "Human route title"),
                    Field("System.Description", "Html", "<p>Human route HTML</p>"),
                    Field(stateField, "Text", "Doing")));
                saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                HumanPatchAttempts.ShouldBe(1, "All three scalar changes use one actual native PATCH.");
                var observed = await ReadHumanHostAsync(fixture.Root, fixture.Stamp, false, ct);
                observed.IsSuccess.ShouldBeTrue(observed.IsSuccess ? "" : observed.Error.Message);
                var snapshot = observed.Value.Snapshot;
                snapshot.Drafts.ShouldBeEmpty();
                snapshot.Journal.Single().Status.ShouldBe(OpStatus.Verified);
                var actual = snapshot.Items.Single(value => value.Key.NativeId == "42");
                actual.Fields["System.Title"].Text.ShouldBe("Human route title");
                actual.Fields["System.Description"].ShouldBe(FieldValue.RichText("<p>Human route HTML</p>", FieldKind.Html));
                actual.Fields[stateField].Text.ShouldBe("Doing");
                var fresh = await Ok("/editor", HttpMethod.Post, open);
                await Ok("/editor/close", HttpMethod.Post, new JsonObject { ["ticket"] = fresh["ticket"]!.DeepClone() });
                (await Send("/editor/save", HttpMethod.Post, Change(fresh, Field("System.Title", "Text", "Stale"))))
                    .Status.ShouldBe(HttpStatusCode.Conflict);
                HumanPatchAttempts.ShouldBe(1, "Closing the fresh handle fences stale saves before provider dispatch.");
            }
            _agentHttpOutput?.WriteLine($"SCALAR {scenario}: local={gateway.LocalReads}; schema={gateway.SchemaReads}; " +
                $"online={gateway.OnlineReads}; providerHttp={_transport.TotalRequests}; " +
                $"captures={await Captures()}; providerPatchAttempts={HumanPatchAttempts}");
        }
        finally
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(55))
                Phase("TIMING WARNING: case exceeded 55s target");
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }
}
