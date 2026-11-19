using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
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
    [Theory]
    [InlineData("association")]
    [InlineData("outgoing")]
    [InlineData("incoming")]
    [InlineData("hierarchy")]
    [InlineData("guards")]
    [InlineData("protected")]
    [InlineData("observed-removal")]
    public async Task NativeAdoLinkHttpKeepsExactAuthority(string scenario)
    {
        var caseClock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = deadline.Token;
        var fixture = await CreateHumanHostAsync().WaitAsync(ct);
        _transport.SetState(42, "Doing");
        if (scenario == "observed-removal")
            _transport.SetNativeRelationship(43, 42, "Native.DependencyForward");
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var changes = new CatalogChanges();
        var gateway = new NativeHttpGateway(this);
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "link-http-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "link-http-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, Guid.NewGuid());
        var owned = sessions.Content(human.ViewId, human.ContentCapability, true);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        try
        {
            await app.StartAsync(ct);
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string suffix, JsonNode? body = null,
                HttpMethod? method = null, string? capability = null, bool csrf = true)
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Post, $"/api/v1/views/{human.ViewId:D}" + suffix);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability ?? human.ContentCapability);
                if (csrf) request.Headers.Add("X-Twig-Bench", "1");
                if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var requestClock = Stopwatch.StartNew();
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (requestClock.Elapsed > TimeSpan.FromSeconds(55))
                    _agentHttpOutput?.WriteLine($"TIMING WARNING: ADO link {scenario} {request.Method} {suffix} exceeded 55s request target ({requestClock.ElapsedMilliseconds}ms)");
                text.ShouldNotBeNullOrWhiteSpace(suffix);
                return (response.StatusCode, JsonNode.Parse(text)!);
            }
            async Task<JsonNode> Ok(string suffix, JsonNode? body = null, HttpMethod? method = null)
            {
                var result = await Send(suffix, body, method);
                result.Status.ShouldBe(HttpStatusCode.OK, suffix + ": " + result.Body);
                return result.Body;
            }
            async Task<StoreSnapshot> Snapshot()
            {
                using var services = EditorServices(fixture.Root);
                using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(ct)).Value;
                return (await origin.Workspace.SnapshotAsync(ct)).Value;
            }
            async Task<Func<bool, Task>> Preserve()
            {
                var snapshot = await Snapshot();
                var captures = (await store.ReadEditorCapturesAsync(ct)).Value;
                var patches = HumanPatchAttempts;
                var writes = _transport.CommittedWrites;
                return async includeVersion =>
                {
                    var after = await Snapshot();
                    if (includeVersion) after.Version.ShouldBe(snapshot.Version);
                    after.Drafts.ShouldBe(snapshot.Drafts);
                    after.Proposals.Select(p => Convert.ToHexString(Twig.Core.Core.ProposalBytes(p)))
                        .ShouldBe(snapshot.Proposals.Select(p => Convert.ToHexString(Twig.Core.Core.ProposalBytes(p))));
                    LinkHttpJournal(after).ShouldBe(LinkHttpJournal(snapshot));
                    var retained = (await store.ReadEditorCapturesAsync(ct)).Value;
                    retained.Length.ShouldBe(captures.Length);
                    foreach (var capture in captures)
                        retained.Single(value => value.Id == capture.Id).SameAs(capture).ShouldBeTrue();
                    HumanPatchAttempts.ShouldBe(patches);
                    _transport.CommittedWrites.ShouldBe(writes);
                };
            }
            await Ok("/context", new JsonObject { ["root"] = fixture.Root }, HttpMethod.Put);
            var view = await Ok($"/benches/{fixture.Bench:D}", method: HttpMethod.Get);
            var source = view["sources"]!.AsArray().Single()!;
            var target = source["items"]!.AsArray().Single(item => item!["locator"]!.GetValue<string>() == "42")!;
            var endpoint = source["items"]!.AsArray().Single(item => item!["locator"]!.GetValue<string>() == "43")!;
            source["provider"]!.GetValue<string>().ShouldBe("ado");
            _transport.TotalRequests.ShouldBeGreaterThan(0);
            var open = new JsonObject
            {
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["itemId"] = target["id"]!.DeepClone(),
                ["benchId"] = fixture.Bench.ToString("D"),
            };
            async Task<JsonNode> Pair()
            {
                var scalar = await Ok("/editor", open);
                return await Ok("/editor/relationships", new JsonObject
                {
                    ["ticket"] = scalar["ticket"]!.DeepClone(), ["endpointId"] = endpoint["id"]!.DeepClone(),
                });
            }
            static JsonObject Field(string reference, string value) => new()
            {
                ["reference"] = reference,
                ["value"] = new JsonObject { ["kind"] = "Text", ["value"] = value, ["dataType"] = null },
            };
            static JsonObject Change(JsonNode pair, JsonNode link, params JsonNode[] fields) => new()
            {
                ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(),
                ["fields"] = new JsonArray(fields), ["intent"] = new JsonArray(link.DeepClone()),
            };
            static JsonNode Choice(JsonNode pair, string shape, bool outgoing, bool remove)
            {
                var relation = pair["relations"]!.AsArray().Single(kind => kind!["shape"]!.GetValue<string>() == shape)!
                    ["reference"]!.GetValue<string>();
                return pair["choices"]!.AsArray().Single(choice => choice!["relation"]!.GetValue<string>() == relation
                    && choice["outgoing"]!.GetValue<bool>() == outgoing && choice["remove"]!.GetValue<bool>() == remove)!;
            }
            async Task AssertEffect(int effects, string relation, bool outgoing, bool removed, string? title = null)
            {
                HumanPatchAttempts.ShouldBe(effects);
                _transport.CommittedWrites.ShouldBe(effects);
                _transport.PostCount.ShouldBe(0);
                var patch = _transport.RelationshipPatches.Last();
                patch.Id.ShouldBe(42, "Incoming and outgoing deltas use one atomic target PATCH.");
                using var document = JsonDocument.Parse(patch.Body);
                var operations = document.RootElement.EnumerateArray().ToArray();
                operations.ShouldContain(op => op.GetProperty("path").GetString() == "/rev" && op.GetProperty("op").GetString() == "test");
                var snapshot = await Snapshot();
                var item = snapshot.Items.Single(item => item.Key.NativeId == "42");
                var other = snapshot.Items.Single(item => item.Key.NativeId == "43");
                item.RelationshipsComplete.ShouldBeTrue();
                other.RelationshipsComplete.ShouldBeTrue();
                if (title is not null) item.Fields["System.Title"].Text.ShouldBe(title);
                snapshot.Drafts.ShouldBeEmpty();
                snapshot.Proposals.Length.ShouldBe(effects);
                snapshot.Journal.Length.ShouldBe(effects);
                snapshot.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified && row.Attempt != Guid.Empty);
                snapshot.Relationships.Contains(new(outgoing ? item.Key : other.Key, outgoing ? other.Key : item.Key,
                    relation, RelationshipOrigin.Provider)).ShouldBe(!removed);
                var proposal = snapshot.Proposals.Last();
                var op = proposal.Operations.Single();
                op.Kind.ShouldBe(OpKind.Batch);
                op.Item.ShouldBe(item.Key);
                op.Links.ShouldBe([new(other.Key, relation, outgoing, removed)]);
                snapshot.Journal.Last().Digest.ShouldBe(proposal.Digest);
                snapshot.Journal.Last().Receipts.Last().Revision.ShouldNotBeNull().Item.ShouldBe(item.Key);
                (await store.ReadEditorCapturesAsync(ct)).Value.ShouldAllBe(capture => capture.Settled);
            }
            if (scenario == "observed-removal")
            {
                var targetRevision = _transport.RevisionOf(42);
                var endpointRevision = _transport.RevisionOf(43);
                var pair = await Pair();
                var removal = Choice(pair, "Dependency", false, true);
                var relation = removal["relation"]!.GetValue<string>();
                var staged = await Ok("/editor/relationships/stage", Change(pair, removal));
                staged["intent"]!.AsArray().Count.ShouldBe(1);
                var capture = (await store.ReadEditorCapturesAsync(ct)).Value.ShouldHaveSingleItem();
                capture.Ready.ShouldBeTrue();
                capture.Settled.ShouldBeFalse();
                var operation = UserBenchStore.ParseIntent(capture.IntentJson).Operations.ShouldHaveSingleItem();
                operation.Fields.Count.ShouldBe(0);
                var before = await Snapshot();
                var item = before.Items.Single(item => item.Key.NativeId == "42");
                var other = before.Items.Single(item => item.Key.NativeId == "43");
                operation.Item.ShouldBe(item.Key);
                operation.Links.ShouldBe([new(other.Key, relation, false, true)]);
                before.Drafts.ShouldHaveSingleItem().Changes.Count.ShouldBe(0);
                before.Drafts.Single().Links.ShouldBeEmpty();
                before.Proposals.ShouldBeEmpty();
                before.Journal.ShouldBeEmpty();
                before.Relationships.ShouldContain(new Relationship(other.Key, item.Key, relation, RelationshipOrigin.Provider));
                HumanPatchAttempts.ShouldBe(0);
                _transport.CommittedWrites.ShouldBe(0);
                var scalar = await Ok("/editor", open);
                scalar["relationshipsReadOnly"]!.GetValue<bool>().ShouldBeTrue();
                scalar["relationshipIntent"]!.AsArray().Count.ShouldBe(1);
                pair = await Pair();
                JsonNode.DeepEquals(pair["intent"], staged["intent"]).ShouldBeTrue();
                pair["incident"]!.AsArray().ShouldContain(edge =>
                    edge!["relation"]!.GetValue<string>() == relation
                    && edge["from"]!.GetValue<string>() == pair["endpoint"]!["itemId"]!.GetValue<string>()
                    && edge["to"]!.GetValue<string>() == pair["target"]!["itemId"]!.GetValue<string>());
                (await store.ReadEditorCapturesAsync(ct)).Value.ShouldHaveSingleItem().SameAs(capture).ShouldBeTrue();
                var body = Change(pair, pair["intent"]![0]!);
                body["fields"]!.AsArray().ShouldBeEmpty();
                var saved = await Ok("/editor/relationships/save", body);
                saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                await AssertEffect(1, relation, false, true);
                var after = await Snapshot();
                after.Proposals.ShouldHaveSingleItem().Operations.ShouldHaveSingleItem().Fields.Count.ShouldBe(0);
                var journal = after.Journal.ShouldHaveSingleItem();
                journal.Receipts.Length.ShouldBe(2);
                journal.Receipts.ShouldAllBe(receipt => receipt.Attempt == journal.Attempt
                    && receipt.Digest == journal.Digest && receipt.Operation == journal.Operation);
                var settled = (await store.ReadEditorCapturesAsync(ct)).Value.ShouldHaveSingleItem();
                settled.Id.ShouldBe(capture.Id);
                settled.Endpoints.ShouldBe(capture.Endpoints);
                var retained = UserBenchStore.ParseIntent(settled.IntentJson).Operations.ShouldHaveSingleItem();
                retained.ExpectedRevision.ShouldBe(operation.ExpectedRevision);
                retained.Fields.Count.ShouldBe(0);
                retained.Links.ShouldBe(operation.Links);
                _transport.RevisionOf(42).ShouldBe(targetRevision + 1);
                _transport.RevisionOf(43).ShouldBe(endpointRevision + 1);
            }
            else if (scenario is "association" or "outgoing" or "incoming" or "hierarchy")
            {
                var (shape, outgoing) = scenario switch
                {
                    "association" => ("Association", true), "hierarchy" => ("Hierarchy", false),
                    "outgoing" => ("Dependency", true), _ => ("Dependency", false),
                };
                var pair = await Pair();
                var link = Choice(pair, shape, outgoing, false);
                var preserved = await Preserve();
                var prospective = await Ok("/editor/evaluate", new JsonObject
                {
                    ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(),
                    ["fields"] = new JsonArray(Field(pair["fields"]!["stateField"]!.GetValue<string>(), "Done")),
                });
                prospective["fields"]!.AsArray().Any(field => field!["required"]!.GetValue<bool>()).ShouldBeTrue();
                prospective["messages"]!.AsArray().ShouldNotBeEmpty("A genuine required close gate is evaluated without staging.");
                await preserved(true);
                (await Send("/editor/save", new JsonObject
                {
                    ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(), ["fields"] = new JsonArray(),
                })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
                var title = $"Atomic {shape} {outgoing}";
                var targetRevision = _transport.RevisionOf(42);
                var endpointRevision = _transport.RevisionOf(43);
                var staged = await Ok("/editor/relationships/stage", Change(pair, link, Field("System.Title", title)));
                staged["intent"]!.AsArray().Count.ShouldBe(1);
                HumanPatchAttempts.ShouldBe(0);
                var capture = (await store.ReadEditorCapturesAsync(ct)).Value.Single();
                capture.Ready.ShouldBeTrue();
                capture.Settled.ShouldBeFalse();
                var scalar = await Ok("/editor", open);
                scalar["relationshipsReadOnly"]!.GetValue<bool>().ShouldBeTrue();
                scalar["relationshipIntent"]!.AsArray().Count.ShouldBe(1);
                pair = await Pair();
                (await store.ReadEditorCapturesAsync(ct)).Value.Single().Id.ShouldBe(capture.Id);
                var saved = await Ok("/editor/relationships/save", Change(pair, pair["intent"]![0]!));
                saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                await AssertEffect(1, link["relation"]!.GetValue<string>(), outgoing, false, title);
                _transport.RevisionOf(42).ShouldBe(targetRevision + 1);
                _transport.RevisionOf(43).ShouldBe(endpointRevision + 1);
                pair = await Pair();
                await Ok("/editor/relationships/stage", Change(pair, Choice(pair, shape, outgoing, true)));
                pair = await Pair();
                await Ok("/editor/relationships/save", Change(pair, pair["intent"]![0]!));
                await AssertEffect(2, link["relation"]!.GetValue<string>(), outgoing, true);
                _transport.RevisionOf(42).ShouldBe(targetRevision + 2);
                _transport.RevisionOf(43).ShouldBe(endpointRevision + 2);
            }
            else
            {
                var pair = await Pair();
                var body = Change(pair, Choice(pair, "Association", true, false));
                var pristine = await Preserve();
                var requests = _transport.TotalRequests;
                foreach (var route in new[] { "stage", "save" })
                    (await Send("/editor/relationships/" + route, body, capability: canvas.PresentationCapability))
                        .Status.ShouldBe(HttpStatusCode.Forbidden);
                (await Send("/editor/relationships/stage", body, csrf: false)).Status.ShouldBe(HttpStatusCode.Forbidden);
                _transport.TotalRequests.ShouldBe(requests, "SDK/CSRF refusals precede provider acquisition.");
                var invalid = body.DeepClone(); invalid["unexpected"] = true;
                (await Send("/editor/relationships/stage", invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
                invalid = body.DeepClone(); invalid["intent"] = new JsonArray((JsonNode?)null);
                (await Send("/editor/relationships/stage", invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
                invalid = body.DeepClone(); invalid["intent"]![0]!.AsObject().Remove("outgoing");
                (await Send("/editor/relationships/stage", invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
                invalid = body.DeepClone(); invalid["intent"]![0]!["remove"] = null;
                (await Send("/editor/relationships/stage", invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
                invalid = body.DeepClone(); invalid["fields"] = new JsonArray((JsonNode?)null);
                (await Send("/editor/relationships/stage", invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
                await pristine(true);
                if (scenario == "protected")
                {
                    _transport.LoseNextPatchAcknowledgement = true;
                    var attempt = await Send("/editor/relationships/save", body);
                    (attempt.Status != HttpStatusCode.OK || !attempt.Body["verified"]!.GetValue<bool>()).ShouldBeTrue();
                    HumanPatchAttempts.ShouldBe(1);
                    _transport.CommittedWrites.ShouldBe(1, "The original effect occurred despite lost acknowledgement.");
                    var snapshot = await Snapshot();
                    snapshot.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Indeterminate);
                    snapshot.Journal.Single().Receipts.ShouldNotBeEmpty();
                    var preserved = await Preserve();
                    (await Send("/editor/relationships/stage", body)).Status.ShouldNotBe(HttpStatusCode.OK);
                    (await Send("/editor/relationships/save", body)).Status.ShouldNotBe(HttpStatusCode.OK);
                    await preserved(true);
                }
                else
                {
                    await Ok("/editor/relationships/stage", body);
                    var preserved = await Preserve();
                    _transport.SetItem(43, "Changed endpoint", _transport.RevisionOf(43) + 1, Actor);
                    (await Send("/editor/relationships/save", body)).Status.ShouldBe(HttpStatusCode.Conflict);
                    await preserved(false);
                    await Ok("/editor/close", new JsonObject { ["ticket"] = pair["fields"]!["ticket"]!.DeepClone() });
                    (await Send("/editor/relationships/save", body)).Status.ShouldBe(HttpStatusCode.Conflict);
                    await preserved(false);
                    HumanPatchAttempts.ShouldBe(0);
                }
            }
            var final = await Snapshot();
            _agentHttpOutput?.WriteLine($"ADO LINK HTTP {scenario}: providerHttp={_transport.TotalRequests}, " +
                $"patchAttempts={HumanPatchAttempts}, committedWrites={_transport.CommittedWrites}, creates={_transport.PostCount}, " +
                $"captures={(await store.ReadEditorCapturesAsync(ct)).Value.Length}, " +
                $"proposals={final.Proposals.Length}, journal={final.Journal.Length}, receipts={final.Journal.Sum(row => row.Receipts.Length)}");
        }
        finally
        {
            if (caseClock.Elapsed > TimeSpan.FromSeconds(55))
                _agentHttpOutput?.WriteLine($"TIMING WARNING: ADO link {scenario} case exceeded 55s target ({caseClock.ElapsedMilliseconds}ms)");
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static string LinkHttpJournal(StoreSnapshot state)
        => string.Join("\n", state.Journal.Select(row => $"{row.Digest}:{row.Operation}:{row.Attempt:D}:{row.Status}:"
            + string.Join("|", row.Receipts.Select(receipt =>
                $"{receipt.Digest}:{receipt.Operation}:{receipt.Attempt:D}:{receipt.Status}:{receipt.Revision}:{receipt.PublishedKey}:"
                + string.Join("|", receipt.Evidence) + $":{receipt.Error?.Code}:{receipt.Error?.Message}:"
                + string.Join("|", receipt.Error?.Evidence ?? [])))));
}
