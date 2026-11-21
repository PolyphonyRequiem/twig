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
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    private readonly ITestOutputHelper? _agentHttpOutput;

    public NativePublicationConsumerTests(ITestOutputHelper? output = null) => _agentHttpOutput = output;

    // Adopted from twig-bench-canvas AgentRouteTests.cs, blob f7a6d4fcba2e67bb253a1c7e807613b99ee3c259.
    [Theory]
    [InlineData("diagnostics")]
    [InlineData("access")]
    [InlineData("membership")]
    [InlineData("incomplete")]
    [InlineData("remote-summary")]
    [InlineData("seed-denied")]
    [InlineData("seed-withdrawal")]
    [InlineData("rules")]
    [InlineData("rule-guards")]
    [InlineData("draft")]
    [InlineData("connections")]
    [InlineData("connection-guards")]
    [InlineData("owner-automatic")]
    [InlineData("mcp")]
    public async Task NativeAgentHttpPreservesScopedNativeAuthorityWithoutProviderWrites(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        var fixture = await CreateHumanHostAsync().WaitAsync(ct);
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new NativeHttpGateway(this);
        var changes = new CatalogChanges();
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "agent-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "agent-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, Guid.NewGuid());
        var ownerSession = sessions.Content(human.ViewId, human.ContentCapability, true);
        var web = Path.Combine(directory, "agent-resource");
        Directory.CreateDirectory(web);
        const string fixtureHtml = "<!doctype html><html><body>Owned finite resource-serving fixture.</body></html>";
        var fixtureBytes = Encoding.UTF8.GetBytes(fixtureHtml);
        await File.WriteAllBytesAsync(Path.Combine(web, "mcp-work-item-details.html"), fixtureBytes, ct);
        await using var app = NativeBenchHost.Build(new(0, directory, web), panel, sessions, changes,
            scenario is "connections" or "connection-guards" or "owner-automatic"
                ? new NativeConnectionSetupGateway(new NativeConnectionSetup(directory)) : null);
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method,
                JsonNode? body = null, bool owner = false, string? capability = null)
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    capability ?? (owner ? human.ContentCapability : canvas.PresentationCapability));
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                return (response.StatusCode, text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!);
            }
            var admitted = await Send("/context", HttpMethod.Put,
                new JsonObject { ["root"] = fixture.Root }, owner: true);
            Check(admitted.Status == HttpStatusCode.OK, admitted.Body.ToJsonString());
            (await Send("/selection", HttpMethod.Put, new JsonObject { ["expectedBenchId"] = null, ["benchId"] = fixture.Bench }, owner: true)).Status.ShouldBe(HttpStatusCode.OK);
            var view = await Send($"/benches/{fixture.Bench:D}", HttpMethod.Get, owner: true);
            Check(view.Status == HttpStatusCode.OK, view.Body.ToJsonString());
            var source = view.Body["sources"]![0]!;
            var item = source["items"]!.AsArray().Single(value => value!["locator"]!.GetValue<string>() == "42")!;
            var bench = fixture.Bench;
            var connection = fixture.Stamp.Connection;
            JsonObject Grant(bool read, bool draft = false, bool membership = false) => new()
            {
                ["benchId"] = bench.ToString("D"), ["read"] = read,
                ["draft"] = draft, ["membership"] = membership,
            };
            async Task<JsonNode> Context()
            {
                var result = await Send("/agent/context", HttpMethod.Get);
                Check(result.Status == HttpStatusCode.OK, result.Body.ToJsonString());
                Check(result.Body["benchId"]!.GetValue<Guid>() == bench, "Context must use the actual native selection.");
                return result.Body;
            }
            static JsonObject Snapshot(JsonNode context) => new() { ["snapshotId"] = context["snapshotId"]!.DeepClone() };
            JsonObject Target(JsonNode context) => new()
            {
                ["snapshotId"] = context["snapshotId"]!.DeepClone(),
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["itemId"] = item["id"]!.DeepClone(),
            };
            JsonObject Stage(JsonNode context)
            {
                var body = Target(context);
                body["fields"] = new JsonArray(new JsonObject
                {
                    ["reference"] = "System.Title",
                    ["value"] = new JsonObject
                    { ["kind"] = "Text", ["value"] = "Agent-staged native title", ["dataType"] = null },
                });
                return body;
            }

            var sequence = 0;
            async Task<(HttpStatusCode Status, JsonNode Body)> Rpc(string method, JsonObject? parameters = null,
                string? capability = null) => await Send("/mcp", HttpMethod.Post, new JsonObject
                { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = parameters }, capability: capability);
            async Task<JsonNode> Call(string name, JsonObject arguments)
            {
                var response = await Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
                Check(response.Status == HttpStatusCode.OK, response.Body.ToJsonString());
                return response.Body["result"] ?? response.Body;
            }

            switch (scenario)
            {
                case "diagnostics": await Diagnostics(); break;
                case "access": await Access(); break;
                case "membership": await Membership(); break;
                case "incomplete": await Incomplete(); break;
                case "remote-summary": await RemoteSummary(); break;
                case "seed-denied":
                case "seed-withdrawal": await SeedDetail(); break;
                case "rules": await Rules(false); break;
                case "rule-guards": await Rules(true); break;
                case "draft": await Draft(); break;
                case "connections":
                case "connection-guards": await Connections(); break;
                case "owner-automatic": await OwnerAutomatic(); break;
                case "mcp": await Mcp(); break;
                default: throw new InvalidOperationException("Unknown Agent scenario.");
            }
            HumanPatchAttempts.ShouldBe(0, "All Agent operations leave actual provider writes at zero.");
            _transport.TotalRequests.ShouldBeGreaterThan(0, "The fixture must exercise actual native provider transport.");
            var captures = await store.ReadEditorCapturesAsync(ct);
            Check(captures.IsSuccess, "Native capture counters must come from the real registry.");
            captures.Value.Length.ShouldBe(scenario is "draft" or "seed-denied" or "seed-withdrawal" ? 1 : 0);
            _agentHttpOutput?.WriteLine(
                $"AGENT {scenario}: local={gateway.LocalReads}; schema={gateway.SchemaReads}; online={gateway.OnlineReads}; " +
                $"profileReads={gateway.ProfileReads}; " +
                $"providerHttp={_transport.TotalRequests}; providerItemReads={_transport.ItemReadIds.Count}; " +
                $"captures={captures.Value.Length}; providerPatchAttempts={HumanPatchAttempts}");

            async Task SeedDetail()
            {
                using (var editor = (await OpenHumanHostAsync(fixture.Root, bench, fixture.Stamp, ct)).Value)
                {
                    var seed = (await editor.CreateSeedAsync(item["type"]!.GetValue<string>(), ct)).Value;
                    (await editor.StageAsync(seed.Ticket, FieldValues.Empty.Set("System.Title",
                        FieldValue.String("Private seed")).Set("System.State", FieldValue.String("Doing"))
                        .Set("System.AssignedTo", FieldValue.Identity(Sibling)), ct)).IsSuccess.ShouldBeTrue();
                    var definition = (await store.ReadAsync(ct)).Value.Benches.Single();
                    (await store.SaveAsync(new(bench, definition.Name, [new(SelectorKind.Pin, seed.Item, connection: connection)],
                        connections: definition.Connections), definition.Revision, ct)).IsSuccess.ShouldBeTrue();
                }
                (await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status.ShouldBe(HttpStatusCode.OK);
                var members = (await Call("read_bench", new()))["structuredContent"]!;
                var seedMember = members["items"]!.AsArray().Single()!;
                seedMember["state"].ShouldBeNull();
                seedMember["assignedTo"].ShouldBeNull();
                JsonObject SeedTarget() => new()
                {
                    ["snapshotId"] = members["snapshotId"]!.DeepClone(),
                    ["sourceId"] = seedMember["sourceId"]!.DeepClone(), ["itemId"] = seedMember["itemId"]!.DeepClone(),
                };
                async Task<JsonNode> Details()
                {
                    var args = SeedTarget();
                    args["fields"] = new JsonArray("System.Title");
                    args["assignedToField"] = "System.AssignedTo";
                    return await Call("show_work_item_details", args);
                }
                var requests = _transport.TotalRequests;
                (await Send("/agent/item", HttpMethod.Post, SeedTarget())).Body["item"]!["isLocal"]!.GetValue<bool>().ShouldBeTrue();
                (await Details())["structuredContent"]!["isLocal"]!.GetValue<bool>().ShouldBeTrue();
                _transport.TotalRequests.ShouldBe(requests);
                var evidence = Directory.GetFiles(Path.Combine(directory, "agent-evidence"), "*.json").Single();
                if (scenario == "seed-denied")
                    await File.WriteAllBytesAsync(evidence + ".denied", [1], ct);
                else
                    await File.WriteAllTextAsync(evidence + ".withdrawn", """["42"]""", ct);
                var refused = await Send("/agent/item", HttpMethod.Post, SeedTarget());
                refused.Status.ShouldBe(HttpStatusCode.Forbidden);
                refused.Body["item"].ShouldBeNull();
                var details = await Details();
                details["isError"]!.GetValue<bool>().ShouldBeTrue();
                details["structuredContent"].ShouldBeNull();
                _transport.TotalRequests.ShouldBe(requests);
            }

            async Task RemoteSummary()
            {
                using (var services = EditorServices(fixture.Root))
                using (var native = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(ct)).Value)
                {
                    var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
                    (await native.Workspace.StageEditAsync(key, FieldValues.Empty
                        .Set("System.State", FieldValue.String("Doing"))
                        .Set("System.AssignedTo", FieldValue.Identity(Sibling)), ct)).IsSuccess.ShouldBeTrue();
                }
                (await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status.ShouldBe(HttpStatusCode.OK);
                var http = await Send("/agent/bench", HttpMethod.Post, Snapshot(await Context()));
                http.Status.ShouldBe(HttpStatusCode.OK);
                var mcp = await Call("read_bench", new());
                foreach (var page in new[] { http.Body, mcp["structuredContent"]! })
                {
                    var summary = page["items"]!.AsArray().Single(member => member!["locator"]!.GetValue<string>() == "42")!;
                    summary["hasDraft"]!.GetValue<bool>().ShouldBeTrue();
                    summary["state"]!["value"]!.GetValue<string>().ShouldBe("To Do");
                    summary["assignedTo"]!["value"]!.GetValue<string>().ShouldBe(Actor);
                }
            }

            async Task Incomplete()
            {
                var definition = (await store.ReadAsync(ct)).Value.Benches.Single();
                var missing = new Connection(connection.Provider, connection.ResourceScope, "missing-project", connection.IdentityRef);
                (await store.SaveAsync(new(bench, definition.Name, definition.Selectors,
                    connections: definition.Connections.Add(missing)), definition.Revision, ct)).IsSuccess.ShouldBeTrue();
                (await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status.ShouldBe(HttpStatusCode.OK);
                var page = await Send("/agent/bench", HttpMethod.Post, Snapshot(await Context()));
                page.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
                page.Body["total"].ShouldBeNull();
                var result = await Call("read_bench", new());
                result["isError"]!.GetValue<bool>().ShouldBeTrue();
                result["structuredContent"].ShouldBeNull();
            }

            async Task Diagnostics()
            {
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden, "Agent reads default deny.");
                var uiState = await Send("/agent/ui-state", HttpMethod.Get);
                Check(uiState.Status == HttpStatusCode.OK && uiState.Body["signedIn"]!.GetValue<bool>()
                    && uiState.Body["connectionSelected"]!.GetValue<bool>() && uiState.Body["benchSelected"]!.GetValue<bool>()
                    && !uiState.Body["read"]!.GetValue<bool>() && uiState.Body["accessState"]!.GetValue<string>() == "Initializing"
                    && uiState.Body["display"] is null, "Diagnose selected, signed-in, uninitialized access without inventing a rendered display.");
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden,
                    "Diagnostics cannot initialize their own content grant.");
                var display = new JsonObject
                {
                    ["screen"] = "Bench", ["surface"] = "work", ["loading"] = false,
                    ["connectionSelected"] = true, ["benchSelected"] = true, ["visibleItems"] = 2,
                    ["hasError"] = true, ["errorCode"] = "AgentPermissionRequired",
                };
                Check((await Send("/agent/display", HttpMethod.Put, display)).Status == HttpStatusCode.Forbidden,
                    "The agent cannot forge the human display observation.");
                Check((await Send("/agent/display", HttpMethod.Put, display, owner: true)).Status == HttpStatusCode.OK,
                    "Only the owner reports bounded display state.");
                display["errorCode"] = "Work item 42: private title";
                Check((await Send("/agent/display", HttpMethod.Put, display, owner: true)).Status == HttpStatusCode.BadRequest,
                    "Raw errors and content are not accepted as diagnostic codes.");
                uiState = await Send("/agent/ui-state", HttpMethod.Get);
                Check(uiState.Body["display"]!["state"]!["visibleItems"]!.GetValue<int>() == 2
                    && uiState.Body["display"]!["state"]!["errorCode"]!.GetValue<string>() == "AgentPermissionRequired"
                    && uiState.Body["display"]!["reportedAt"] is not null
                    && !uiState.Body.ToJsonString().Contains("private title", StringComparison.Ordinal)
                    && !uiState.Body.ToJsonString().Contains(human.ContentCapability, StringComparison.Ordinal)
                    && !uiState.Body.ToJsonString().Contains(bench.ToString("D"), StringComparison.Ordinal),
                    "Diagnostics preserve the last valid bounded observation and never return selected identities or secrets.");
            }

            async Task Access()
            {
                var clock = new AgentTestTimeProvider();
                var timed = new ViewSessions(Path.Combine(directory, "agent-lifetime"), timeProvider: clock);
                var storeId = Guid.NewGuid();
                var active = timed.RegisterNativeCanvas(storeId);
                var owner = timed.ConnectOwner(storeId, active.ViewId, Guid.NewGuid());
                var unauthenticated = timed.RegisterNativeCanvas(storeId);
                clock.Now += TimeSpan.FromMinutes(31);
                _ = timed.Presentation(active.ViewId, active.PresentationCapability);
                var expired = Should.Throw<HttpFailure>(() =>
                    timed.Presentation(unauthenticated.ViewId, unauthenticated.PresentationCapability));
                expired.Code.ShouldBe("SessionExpired", "An unauthenticated presentation must still expire.");
                timed.Revoke(timed.Content(owner.ViewId, owner.ContentCapability, true));
                Should.Throw<HttpFailure>(() => timed.Presentation(active.ViewId, active.PresentationCapability))
                    .Code.ShouldBe("SessionRevoked", "Owner sign-out remains authoritative.");
                Check((await Send("/attachment", HttpMethod.Post, new JsonObject(), owner: true)).Status == HttpStatusCode.OK, "Attach layout.");
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden, "Layout attachment is not content consent.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true))).Status == HttpStatusCode.Forbidden, "The agent cannot grant itself access.");
                Check((await Send("/agent/ui", HttpMethod.Get)).Status == HttpStatusCode.Forbidden, "The agent cannot initialize automatic access.");
                var defaults = await Send("/agent/ui", HttpMethod.Get, owner: true);
                Check(defaults.Status == HttpStatusCode.OK && defaults.Body["permissions"]!["read"]!.GetValue<bool>()
                    && defaults.Body["permissions"]!["draft"]!.GetValue<bool>()
                    && defaults.Body["permissions"]!["membership"]!.GetValue<bool>()
                    && defaults.Body["permissions"]!["rules"]!.GetValue<bool>(),
                    "The signed-in selected canvas automatically enables read, local drafting, membership and saved-rule edits.");
                var automaticContext = await Context();
                var uiState = await Send("/agent/ui-state", HttpMethod.Get);
                Check(uiState.Body["accessState"]!.GetValue<string>() == "Ready" && uiState.Body["read"]!.GetValue<bool>()
                    && uiState.Body["draft"]!.GetValue<bool>() && uiState.Body["membership"]!.GetValue<bool>(),
                    "Diagnostics reflect automatic access after authenticated initialization.");
                Check((await Send("/selection", HttpMethod.Put, new JsonObject
                { ["expectedBenchId"] = bench.ToString("D"), ["benchId"] = bench.ToString("D") }, owner: true)).Status == HttpStatusCode.OK,
                    "Native scope selection invalidates old automatic access.");
                Check((await Send("/agent/item", HttpMethod.Post, Target(automaticContext))).Status == HttpStatusCode.Forbidden,
                    "Changing scope cannot reuse the previous automatic snapshot.");
                Check((await Send("/agent/ui", HttpMethod.Get, owner: true)).Body["permissions"]!["draft"]!.GetValue<bool>(),
                    "The human canvas initializes automatic access for the selected scope again.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(false, true), owner: true)).Status == HttpStatusCode.BadRequest, "Draft requires read.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK, "Explicit owner read consent.");
                Check(!(await Send("/agent/ui", HttpMethod.Get, owner: true)).Body["permissions"]!["draft"]!.GetValue<bool>(),
                    "Automatic defaults never override a manual read-only choice.");
                var context = await Context();
                Check(context["connections"]!.AsArray().Count == 1
                    && context["connections"]![0]!["provider"]!.GetValue<string>() == "ado", "Return only this view's admitted sources.");
                Check(!context.ToJsonString().Contains(canvas.PresentationCapability, StringComparison.Ordinal)
                    && !context.ToJsonString().Contains(human.ContentCapability, StringComparison.Ordinal), "Never return capabilities.");
                var page = await Send("/agent/bench", HttpMethod.Post, Snapshot(context));
                Check(page.Status == HttpStatusCode.OK && page.Body["total"]!.GetValue<int>() == 2, page.Body.ToJsonString());
                var paged = Snapshot(context); paged["limit"] = 1;
                var first = await Send("/agent/bench", HttpMethod.Post, paged);
                Check(first.Status == HttpStatusCode.OK && first.Body["items"]!.AsArray().Count == 1
                    && first.Body["total"]!.GetValue<int>() == 2 && first.Body["nextOffset"]!.GetValue<int>() == 1,
                    "The first bounded page retains the actual two-member total.");
                paged["offset"] = first.Body["nextOffset"]!.DeepClone();
                var second = await Send("/agent/bench", HttpMethod.Post, paged);
                Check(second.Status == HttpStatusCode.OK && second.Body["items"]!.AsArray().Count == 1
                    && second.Body["total"]!.GetValue<int>() == 2 && second.Body["nextOffset"] is null
                    && first.Body["items"]![0]!["itemId"]!.GetValue<string>() != second.Body["items"]![0]!["itemId"]!.GetValue<string>(),
                    "Pagination returns the other actual member once and terminates.");
                var target = await Send("/agent/item", HttpMethod.Post, Target(context));
                Check(target.Status == HttpStatusCode.OK && target.Body["item"]!["pin"] is null, "Exact item read must not release an executable selector.");
                var locator = int.Parse(target.Body["item"]!["locator"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                _transport.SetItem(locator, "Fresh provider title", _transport.RevisionOf(locator) + 1, Actor);
                var refreshed = await Send("/agent/item", HttpMethod.Post, Target(context));
                Check(refreshed.Status == HttpStatusCode.OK
                    && refreshed.Body["item"]!["title"]!.GetValue<string>() == "Fresh provider title",
                    "A delegated item read must use the provider response, not retained snapshot fields.");
                _transport.SetItem(locator, "Not readable by this account", _transport.RevisionOf(locator) + 1, Sibling);
                try
                {
                    var denied = await Send("/agent/item", HttpMethod.Post, Target(context));
                    Check(denied.Status != HttpStatusCode.OK && denied.Body["item"] is null,
                        "A provider refusal must not fall back to the retained snapshot.");
                }
                finally
                {
                    _transport.SetItem(locator, "Fresh provider title", _transport.RevisionOf(locator) + 1, Actor);
                }
                var other = sessions.RegisterNativeCanvas(context["plan"]!["storeId"]!.GetValue<Guid>());
                Check((await Send("/agent/ui-state", HttpMethod.Get, capability: other.PresentationCapability)).Status == HttpStatusCode.Forbidden,
                    "Diagnostics cannot enumerate another view with a different presentation capability.");
                using (var pending = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/views/{other.ViewId:D}/agent/ui-state"))
                {
                    pending.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.PresentationCapability);
                    using var response = await client.SendAsync(pending, ct);
                    var state = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                    Check(response.StatusCode == HttpStatusCode.OK && !state["signedIn"]!.GetValue<bool>()
                        && state["accessState"]!.GetValue<string>() == "SignInRequired" && state["display"] is null,
                        "A new unauthenticated canvas exposes only operational status without a human grant.");
                }
                Check((await Send("/agent/context", HttpMethod.Get, capability: other.PresentationCapability)).Status == HttpStatusCode.Forbidden,
                    "Another canvas capability cannot borrow this grant.");
                Check((await Send("/agent/stage", HttpMethod.Post, Stage(context))).Status == HttpStatusCode.Forbidden, "Read is not drafting consent.");
                Check((await Send("/agent/unpin", HttpMethod.Post, Target(context))).Status == HttpStatusCode.Forbidden, "Read is not membership consent.");
            }

            async Task Membership()
            {
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true, membership: true), owner: true)).Status == HttpStatusCode.OK, "Membership consent.");
                var context = await Context();
                var unpinned = await Send("/agent/unpin", HttpMethod.Post, Target(context));
                Check(unpinned.Status == HttpStatusCode.OK && unpinned.Body["changed"]!.GetValue<bool>()
                    && !unpinned.Body["remoteWrites"]!.GetValue<bool>(), unpinned.Body.ToJsonString());
                Check((await Send("/agent/unpin", HttpMethod.Post, Target(context))).Status == HttpStatusCode.Conflict,
                    "A consumed snapshot cannot replay membership changes.");
                context = await Context();
                var pin = Snapshot(context); pin["sourceId"] = source["sourceId"]!.DeepClone(); pin["locator"] = "42";
                Check((await Send("/agent/pin", HttpMethod.Post, pin)).Status == HttpStatusCode.OK, "Restore exact native cached pin.");
                context = await Context(); pin["snapshotId"] = context["snapshotId"]!.DeepClone();
                var duplicate = await Send("/agent/pin", HttpMethod.Post, pin);
                Check(duplicate.Status == HttpStatusCode.OK && !duplicate.Body["changed"]!.GetValue<bool>(), "Duplicate pin is explicitly unchanged.");
                pin["locator"] = "999999";
                var beforeItems = _transport.ItemReadIds.ToArray();
                Check((await Send("/agent/pin", HttpMethod.Post, pin)).Status == HttpStatusCode.NotFound, "Missing cache is not a guessed remote lookup.");
                _transport.ItemReadIds.ToArray().ShouldBe(beforeItems,
                    "Fresh authority probes remain required, but missing cache cannot trigger guessed item acquisition.");
            }

            async Task Draft()
            {
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true, true), owner: true)).Status == HttpStatusCode.OK, "Drafting consent.");
                var context = await Context();
                gateway.RefuseEditor = true;
                var refused = await Send("/agent/stage", HttpMethod.Post, Stage(context));
                Check(refused.Status == HttpStatusCode.ServiceUnavailable
                    && refused.Body["code"]!.GetValue<string>() == "Unavailable"
                    && refused.Body["message"]!.GetValue<string>().Contains("opening the native editor (Unavailable)", StringComparison.Ordinal)
                    && !refused.Body.ToJsonString().Contains("Synthetic native editor refusal.", StringComparison.Ordinal),
                    "An uncertain staging failure identifies its native step and code without raw source errors.");
                gateway.RefuseEditor = false;
                context = await Context();
                var target = await Send("/agent/item", HttpMethod.Post, Target(context));
                Check(context["plan"]!["enabled"]!.GetValue<bool>() && !target.Body["item"]!["hasDraft"]!.GetValue<bool>(),
                    "Inspect the partially enabled Plan and absent draft before a fresh staging attempt.");
                var staged = await Send("/agent/stage", HttpMethod.Post, Stage(context));
                Check(staged.Status == HttpStatusCode.OK, staged.Body.ToJsonString());
                Check((await Send("/agent/stage", HttpMethod.Post, Stage(context))).Status == HttpStatusCode.Conflict, "No automatic stage replay.");
                context = await Context();
                target = await Send("/agent/item", HttpMethod.Post, Target(context));
                Check(context["plan"]!["enabled"]!.GetValue<bool>() && target.Body["item"]!["proposedFields"]!.AsArray()
                    .Any(field => field!["reference"]!.GetValue<string>() == "System.Title"
                        && field["value"]!["value"]!.GetValue<string>() == "Agent-staged native title"), "Stage persists in the actual native Plan.");
                var captures = await store.ReadEditorCapturesAsync(ct);
                Check(captures.IsSuccess && captures.Value.Length == 1, "Exactly one real native staging capture is persisted.");
                var requested = await Send("/agent/review", HttpMethod.Post, Snapshot(context));
                Check(requested.Status == HttpStatusCode.OK && requested.Body["state"]!.GetValue<string>() == "Requested", "Request is not displayed proof.");
                var duplicateReview = await Send("/agent/review", HttpMethod.Post, Snapshot(context));
                Check(duplicateReview.Body["requestId"]!.GetValue<Guid>() == requested.Body["requestId"]!.GetValue<Guid>(), "A pending request is idempotent.");
                var invented = new JsonObject
                {
                    ["requestId"] = requested.Body["requestId"]!.DeepClone(), ["state"] = "Displayed",
                    ["digest"] = "invented", ["message"] = null,
                };
                Check((await Send("/agent/review-result", HttpMethod.Post, invented, owner: true)).Status == HttpStatusCode.BadRequest, "No invented review acknowledgement.");
                Check((await Send("/plan/review", HttpMethod.Post, new JsonObject { ["benchId"] = bench.ToString("D") })).Status == HttpStatusCode.Forbidden,
                    "Agent capability cannot call human review or publisher endpoints.");
                Check((await Send("/plan/apply", HttpMethod.Post, new JsonObject { ["digest"] = "invented" })).Status == HttpStatusCode.Forbidden,
                    "The SDK cannot apply a Plan.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(false), owner: true)).Status == HttpStatusCode.OK, "Revoke consent.");
                Check(!(await Send("/agent/ui", HttpMethod.Get, owner: true)).Body["permissions"]!["read"]!.GetValue<bool>(),
                    "Human UI refresh cannot automatically undo revocation.");
                var uiState = await Send("/agent/ui-state", HttpMethod.Get);
                Check(uiState.Body["signedIn"]!.GetValue<bool>() && !uiState.Body["automaticAccess"]!.GetValue<bool>()
                    && !uiState.Body["read"]!.GetValue<bool>() && uiState.Body["accessState"]!.GetValue<string>() == "AccessOff",
                    "A manual revocation is distinguishable from sign-out and cannot be undone by diagnostics.");
                Check((await Send("/agent/item", HttpMethod.Post, Target(context))).Status == HttpStatusCode.Forbidden, "Revocation blocks retained snapshots.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK, "New read grant.");
                context = await Context();
                HumanActorDenied = true;
                Check((await Send("/agent/item", HttpMethod.Post, Target(context))).Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
                    "Changed source authority cannot release cached snapshot content.");
            }

            async Task Rules(bool guards)
            {
                var original = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                var all = new Selector(SelectorKind.Query, query: new(QueryKind.All), connection: connection);
                var foreign = new Selector(SelectorKind.Query, query: new(QueryKind.All),
                    connection: new(connection.Provider, connection.ResourceScope, connection.Destination, "another-principal"));
                var selectors = guards ? original.Selectors.Take(1).Append(all).Append(foreign).ToArray()
                    : original.Selectors.Take(1).Append(all).ToArray();
                var prepared = await store.SaveAsync(new(bench, original.Name, selectors), original.Revision, ct);
                Check(prepared.IsSuccess, "Rules regression requires a real persisted query, an unrelated pin and an unadmitted source.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true, membership: guards), owner: true)).Status == HttpStatusCode.OK, "Read rules consent.");
                var context = await Context();
                var read = await Send("/agent/rules", HttpMethod.Post, Snapshot(context));
                Check(read.Status == HttpStatusCode.OK, $"Read saved Bench rules, not display filters: HTTP {read.Status} {read.Body}");
                Check(read.Body["unadmittedRuleCount"]!.GetValue<int>() == (guards ? 1 : 0)
                    && !read.Body.ToJsonString().Contains("another-principal", StringComparison.Ordinal),
                    "Unadmitted rules are counted without disclosing their selector, identity or source.");
                var rule = read.Body["rules"]!.AsArray().Single(value => value!["kind"]!.GetValue<string>() == "Query")!;
                var index = rule["ruleIndex"]!.GetValue<int>();
                var field = item["stateField"]!.GetValue<string>();
                var states = source["items"]!.AsArray().Select(value => value!["cachedFields"]!.AsArray()
                    .Single(cached => cached!["reference"]!.GetValue<string>() == field)!["value"]!["value"]!.GetValue<string>()).Distinct().ToArray();
                var excluded = new Query(QueryKind.Or, children: states.Select(state => new Query(QueryKind.Equal, field, FieldValue.String(state))));
                var narrowed = new Selector(SelectorKind.Query, query: new(QueryKind.And, children:
                    [all.Query!, new(QueryKind.Not, children: [excluded])]), connection: connection);
                JsonObject Edit(JsonNode scope, Selector selector, Guid? sourceId = null, int? ruleIndex = null) => new()
                {
                    ["snapshotId"] = scope["snapshotId"]!.DeepClone(),
                    ["sourceId"] = sourceId?.ToString("D") ?? source["sourceId"]!.GetValue<string>(),
                    ["ruleIndex"] = ruleIndex ?? index, ["selector"] = BenchCodec.SerializeSelectors([selector]),
                };
                if (guards)
                    Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, narrowed))).Status == HttpStatusCode.Forbidden,
                        "Read and pin/unpin consent cannot authorize saved rule edits.");
                var grant = Grant(true, membership: true); grant["rules"] = true;
                Check((await Send("/agent/permissions", HttpMethod.Put, grant, owner: true)).Status == HttpStatusCode.OK, "Separate human rule-edit consent.");
                context = await Context();
                if (guards)
                {
                    Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, narrowed, Guid.NewGuid()))).Status == HttpStatusCode.Forbidden,
                        "Rules cannot address unadmitted sources.");
                    Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, narrowed, ruleIndex: 0))).Status == HttpStatusCode.BadRequest,
                        "A query edit cannot replace a direct pin or subtree.");
                    Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, foreign))).Status == HttpStatusCode.BadRequest,
                        "A rule cannot change its exact source/principal binding.");
                    var concurrent = await store.SaveAsync(new(bench, original.Name, selectors), prepared.Value.Revision, ct);
                    Check(concurrent.IsSuccess, "Concurrent real writer must advance the Bench before stale CAS.");
                    Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, all))).Status == HttpStatusCode.Conflict,
                        "A changed Bench revision cannot overwrite concurrent saved rules.");
                    var retained = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                    Check(retained.Revision == concurrent.Value.Revision
                        && BenchCodec.SerializeSelectors(retained.Selectors) == BenchCodec.SerializeSelectors(selectors),
                        "Failed CAS preserves the competing writer's exact definition.");
                    Check((await store.SaveAsync(new(bench, original.Name, original.Selectors), concurrent.Value.Revision, ct)).IsSuccess,
                        "Restore only the synthetic fixture for remaining scenarios.");
                    return;
                }
                Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, all))).Body["changed"]!.GetValue<bool>() == false,
                    "Identical query edits explicitly report no change.");
                var before = await Send("/agent/bench", HttpMethod.Post, Snapshot(context));
                Check(before.Body["total"]!.GetValue<int>() == 2, "A query selects the second item beyond the one direct pin.");
                var changed = await Send("/agent/query-rule", HttpMethod.Post, Edit(context, narrowed));
                Check(changed.Status == HttpStatusCode.OK && changed.Body["changed"]!.GetValue<bool>()
                    && !changed.Body["remoteWrites"]!.GetValue<bool>(), changed.Body.ToJsonString());
                Check((await Send("/agent/query-rule", HttpMethod.Post, Edit(context, narrowed))).Status == HttpStatusCode.Conflict,
                    "Consumed rule snapshots cannot replay.");
                var saved = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                Check(saved.Revision == prepared.Value.Revision + 1
                    && BenchCodec.SerializeSelectors(saved.Selectors.Where((_, position) => position != index))
                        == BenchCodec.SerializeSelectors(selectors.Where((_, position) => position != index))
                    && BenchCodec.SerializeSelectors([saved.Selectors[index]]) == BenchCodec.SerializeSelectors([narrowed]),
                    "Only the selected query is durably changed; all other selectors remain exact.");
                context = await Context();
                var after = await Send("/agent/bench", HttpMethod.Post, Snapshot(context));
                Check(after.Body["total"]!.GetValue<int>() == 1 && after.Body["items"]![0]!["directPin"]!.GetValue<bool>(),
                    "Persisted negated state rules remove query-derived membership while leaving the independent pin intact.");
                var listing = await Send("/agent/rules", HttpMethod.Post, Snapshot(context));
                Check(listing.Body["rules"]!.AsArray().Single(value => value!["ruleIndex"]!.GetValue<int>() == index)!["selector"]!.GetValue<string>()
                    == BenchCodec.SerializeSelectors([narrowed]), "Fresh rule reads reflect persisted native membership intent.");
                Check((await store.SaveAsync(new(bench, original.Name, original.Selectors), saved.Revision, ct)).IsSuccess,
                    "Restore only the synthetic fixture's original selectors for remaining scenarios.");
            }

            async Task Connections()
            {
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK,
                    "Connection requests need read access, not membership or rule-edit grants.");
                var before = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                async Task Unchanged()
                {
                    var current = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                    Check(current.Revision == before.Revision && current.Name == before.Name
                        && BenchCodec.SerializeSelectors(current.Selectors) == BenchCodec.SerializeSelectors(before.Selectors),
                        "Connection requests preserve the exact saved Bench name, revision and selectors.");
                }
                var context = await Context();
                var request = Snapshot(context);
                request["provider"] = "github"; request["repository"] = "PolyphonyRequiem/twig-bench-canvas";
                var proposed = await Send("/agent/connection-request", HttpMethod.Post, request);
                Check(proposed.Status == HttpStatusCode.OK && proposed.Body["state"]!.GetValue<string>() == "Requested",
                    "An agent requests an owner-approved connection without granting itself admission. " + proposed.Body);
                await Unchanged();
                Check((await Send("/connections/approve", HttpMethod.Post, new JsonObject
                { ["requestId"] = proposed.Body["requestId"]!.DeepClone(), ["account"] = " " }, owner: true)).Status == HttpStatusCode.BadRequest,
                    "Malformed explicit account input is refused before starting setup.");
                Check((await Send("/connections/approve", HttpMethod.Post, new JsonObject
                { ["requestId"] = proposed.Body["requestId"]!.DeepClone(), ["account"] = "PolyphonyRequiem" })).Status == HttpStatusCode.Forbidden,
                    "The presentation capability cannot approve setup or acquire provider credentials.");
                var duplicate = await Send("/agent/connection-request", HttpMethod.Post, request);
                Check(duplicate.Status == HttpStatusCode.OK && duplicate.Body["requestId"]!.GetValue<Guid>()
                    == proposed.Body["requestId"]!.GetValue<Guid>(), "The same pending request is returned, not dispatched again.");
                request["provider"] = "twig"; request.Remove("repository"); request["database"] = "agent-fixture";
                Check((await Send("/agent/connection-request", HttpMethod.Post, request)).Status == HttpStatusCode.Conflict,
                    "A different request cannot replace a pending human review.");
                Check((await Send("/connections/cancel", HttpMethod.Post, new JsonObject
                { ["requestId"] = proposed.Body["requestId"]!.DeepClone() }, owner: true)).Status == HttpStatusCode.OK,
                    "Only the owner can dismiss the requested connection.");
                request["provider"] = "github"; request["repository"] = "../wrong"; request.Remove("database");
                Check((await Send("/agent/connection-request", HttpMethod.Post, request)).Status == HttpStatusCode.BadRequest,
                    "Invalid repository coordinates refuse before setup or source discovery.");
                request["provider"] = "twig"; request.Remove("repository"); request["database"] = "agent-fixture";
                var local = await Send("/agent/connection-request", HttpMethod.Post, request);
                Check(local.Status == HttpStatusCode.OK && local.Body["provider"]!.GetValue<string>() == "twig",
                    "Native Twig is a first-class requested provider.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(false), owner: true)).Status == HttpStatusCode.OK,
                    "The owner can revoke access while a request is pending.");
                Check((await Send("/connections/approve", HttpMethod.Post, new JsonObject
                { ["requestId"] = local.Body["requestId"]!.DeepClone(), ["account"] = null }, owner: true)).Status == HttpStatusCode.Conflict,
                    "Revocation invalidates the old connection request, even for a delayed human approval.");
                await Unchanged();
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK, "Restore read access explicitly.");
                context = await Context();
                request = Snapshot(context); request["provider"] = "twig"; request["database"] = "agent-fixture";
                local = await Send("/agent/connection-request", HttpMethod.Post, request);
                var approval = new JsonObject { ["requestId"] = local.Body["requestId"]!.DeepClone(), ["account"] = null };
                var started = await Send("/connections/approve", HttpMethod.Post, approval, owner: true);
                Check(started.Status == HttpStatusCode.Accepted, "Owner dispatches the real managed Twig setup once. " + started.Body);
                Check((await Send("/connections/approve", HttpMethod.Post, approval, owner: true)).Status == HttpStatusCode.Conflict,
                    "A duplicate approval cannot initialize another client context.");
                JsonNode ready;
                using (var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    do
                    {
                        await Task.Delay(50, setupDeadline.Token);
                        ready = (await Send("/connections", HttpMethod.Get, owner: true)).Body;
                    } while (ready["operation"]!["pending"]!.GetValue<bool>());
                }
                Check(ready["request"]!["state"]!.GetValue<string>() == "Ready", "Real native setup/readback must qualify before admission. " + ready);
                var root = ready["operation"]!["source"]!["workspaceRoot"]!.GetValue<string>();
                Check(Directory.Exists(Path.Combine(root, ".git")) && Directory.Exists(Path.Combine(root, ".twig"))
                    && new DirectoryInfo(root).Parent!.Name == "sources", "Setup uses its own private client context, not a code checkout.");
                await Unchanged();
                var admission = new JsonObject
                {
                    ["requestId"] = approval["requestId"]!.DeepClone(),
                    ["operationId"] = ready["operation"]!["operationId"]!.DeepClone(), ["includeAll"] = true,
                };
                Check((await Send("/connections/add", HttpMethod.Post, admission, owner: true)).Status == HttpStatusCode.BadRequest,
                    "Connection admission cannot add an implicit all-items Bench rule.");
                admission["includeAll"] = false;
                Check((await Send("/connections/add", HttpMethod.Post, admission)).Status == HttpStatusCode.Forbidden,
                    "An agent cannot admit the prepared source or save its rule.");
                if (scenario == "connection-guards")
                {
                    var wrong = admission.DeepClone().AsObject(); wrong["operationId"] = Guid.NewGuid().ToString("D");
                    Check((await Send("/connections/add", HttpMethod.Post, wrong, owner: true)).Status == HttpStatusCode.NotFound,
                        "Only the exact original native setup can be admitted.");
                    await Unchanged();
                    var concurrent = await store.SaveAsync(new(bench, before.Name, before.Selectors), before.Revision, ct);
                    Check(concurrent.IsSuccess, "A genuine competing native writer must advance the saved Bench revision.");
                    var stale = await Send("/connections/add", HttpMethod.Post, admission, owner: true);
                    Check(stale.Status == HttpStatusCode.Conflict && stale.Body["code"]!.GetValue<string>() == "BenchChanged",
                        "A delayed admission cannot overwrite or extend a concurrently changed Bench.");
                    var after = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                    Check(after.Revision == concurrent.Value.Revision
                        && BenchCodec.SerializeSelectors(after.Selectors) == BenchCodec.SerializeSelectors(before.Selectors)
                        && after.Connections.SequenceEqual(concurrent.Value.Connections),
                        "The rejected admission retains the competing writer's exact definition.");
                    return;
                }
                var admittedSource = await Send("/connections/add", HttpMethod.Post, admission, owner: true);
                Check(admittedSource.Status == HttpStatusCode.OK && admittedSource.Body["changed"]!.GetValue<bool>()
                    && !admittedSource.Body["remoteWrites"]!.GetValue<bool>(), "The owner attaches the exact source without a membership rule. " + admittedSource.Body);
                var saved = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                Check(saved.Revision == before.Revision + 1 && saved.Selectors.Length == before.Selectors.Length
                    && BenchCodec.SerializeSelectors(saved.Selectors) == BenchCodec.SerializeSelectors(before.Selectors)
                    && saved.Connections.Any(value => value.Provider.Value == "twig"),
                    "Admission retains exact ADO rules and attaches native Twig without selecting any of its items.");
                Check((await Send("/connections/add", HttpMethod.Post, admission, owner: true)).Status == HttpStatusCode.Conflict,
                    "A completed admission cannot be replayed.");
                var retained = (await Send("/view-state", HttpMethod.Get, owner: true)).Body;
                Check(retained["choices"]!["displayRoots"]!.AsArray().Any(value => value!.GetValue<string>() == root),
                    "Success persists the admitted source reference independently of receiving or acknowledging its response.");
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden,
                    "Adding a source invalidates the old read scope rather than silently expanding it.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK,
                    "Only the human can authorize the resulting two-source read scope.");
                context = await Context();
                Check(context["connections"]!.AsArray().Count == 2 && context["connections"]!.AsArray()
                    .Any(value => value!["provider"]!.GetValue<string>() == "twig")
                    && context["connections"]!.AsArray().Single(value => value!["viewing"]!.GetValue<bool>())!["provider"]!.GetValue<string>() == "ado",
                    "The resulting Bench admits both providers without switching its ADO navigation anchor.");
            }

            async Task OwnerAutomatic()
            {
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden,
                    "The uninitialized presentation must not already have agent access.");
                var before = (await store.ReadAsync(ct)).Value;
                var authenticated = sessions.AuthenticateAutomaticOwner(before.InstanceId, human.ViewId);
                Check(!authenticated.Restore, "An already signed-in view must retain its live contexts and editor.");
                _ = sessions.Content(human.ViewId, human.ContentCapability, true);
                var context = await Context();
                Check(context["permissions"]!["read"]!.GetValue<bool>() && context["permissions"]!["draft"]!.GetValue<bool>()
                    && context["permissions"]!["rules"]!.GetValue<bool>() && context["permissions"]!["membership"]!.GetValue<bool>(),
                    "The authenticated owner host initializes automatic access without renderer or human permission requests.");
                Check((await Send("/session", HttpMethod.Get)).Status == HttpStatusCode.Forbidden,
                    "Automatic agent initialization must never disclose a human content capability.");
                var request = Snapshot(context); request["provider"] = "twig"; request["database"] = "agent-fixture";
                var proposed = await Send("/agent/connection-request", HttpMethod.Post, request);
                Check(proposed.Status == HttpStatusCode.OK, proposed.Body.ToJsonString());
                var identity = new JsonObject { ["requestId"] = proposed.Body["requestId"]!.DeepClone() };
                var started = await Send("/agent/connection-request-start", HttpMethod.Post, identity);
                Check(started.Status == HttpStatusCode.Accepted,
                    "The authenticated local agent starts the original managed setup without a renderer. " + started.Body);
                Check((await Send("/agent/connection-request-start", HttpMethod.Post, identity)).Status == HttpStatusCode.Conflict,
                    "An automatic setup is never dispatched twice.");
                using (var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    do
                    {
                        await Task.Delay(50, setupDeadline.Token);
                        proposed = await Send("/agent/connection-request-status", HttpMethod.Post, identity);
                        Check(proposed.Status == HttpStatusCode.OK, proposed.Body.ToJsonString());
                    } while (proposed.Body["state"]!.GetValue<string>() == "SettingUp");
                }
                Check(proposed.Body["state"]!.GetValue<string>() == "Ready", proposed.Body.ToJsonString());
                var added = await Send("/agent/connection-request-add", HttpMethod.Post, identity);
                Check(added.Status == HttpStatusCode.OK && added.Body["state"]!.GetValue<string>() == "Added",
                    "The original request admits its verified native source without human setup controls. " + added.Body);
                var saved = (await store.ReadAsync(ct)).Value.Benches.Single(value => value.Id == bench);
                Check(saved.Revision == before.Benches.Single(value => value.Id == bench).Revision + 1
                    && saved.Selectors.Length == before.Benches.Single(value => value.Id == bench).Selectors.Length
                    && saved.Connections.Length == 2,
                    "The source is attached without appending a membership selector.");
                Check((await Send("/agent/connection-request-add", HttpMethod.Post, identity)).Status == HttpStatusCode.Conflict,
                    "Consumed admission is not replayed.");
                context = await Context();
                Check(context["connections"]!.AsArray().Count == 2,
                    "The owner-host scope reinitializes for exactly the saved two-source view without renderer refresh.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(false), owner: true)).Status == HttpStatusCode.OK,
                    "Explicit owner restrictions remain supported.");
                Check((await Send("/agent/context", HttpMethod.Get)).Status == HttpStatusCode.Forbidden,
                    "Automatic owner authentication does not override explicit restrictions.");
                sessions.Revoke(ownerSession);
                Should.Throw<HttpFailure>(() => sessions.AuthenticateAutomaticOwner(before.InstanceId, human.ViewId))
                    .Status.ShouldBe(403, "Owner sign-out must not be automatically reversed.");
            }

            async Task Mcp()
            {
                Check(source["types"]!.AsArray().Any(type => type!["fields"]!.AsArray()
                    .Any(field => field!["reference"]!.GetValue<string>() == "System.Description"))
                    && item["cachedFields"]!.AsArray().Any(field => field!["reference"]!.GetValue<string>() == "System.Title"),
                    "MCP regression requires a real observed item and discovered description field.");
                Check((await Rpc("initialize", capability: new string('a', 64))).Status == HttpStatusCode.Forbidden,
                    "MCP handshake must not bypass exact view capability authentication.");
                var initialized = await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" });
                Check(initialized.Status == HttpStatusCode.OK
                    && initialized.Body["result"]?["protocolVersion"]?.GetValue<string>() == "2025-11-25"
                    && initialized.Body["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "twig",
                    "The production daemon must expose an MCP initialize response. " + initialized.Body);
                var tools = (await Rpc("tools/list")).Body["result"]!["tools"]!.AsArray();
                Check(tools.Count == 2 && tools.All(tool => tool!["annotations"]!["readOnlyHint"]!.GetValue<bool>())
                    && tools.Single(tool => tool!["name"]!.GetValue<string>() == "show_work_item_details")!["_meta"]!["ui"]!["resourceUri"]!
                        .GetValue<string>() == "ui://twig/work-item-details",
                    "The MCP surface contains only real read tools and a discoverable MCP App.");
                var html = (await Rpc("resources/read", new JsonObject { ["uri"] = "ui://twig/work-item-details" })).Body["result"]!["contents"]![0]!;
                Check(html["mimeType"]!.GetValue<string>() == "text/html;profile=mcp-app"
                    && html["_meta"]!["ui"]!["csp"]!["connectDomains"]!.AsArray().Count == 0,
                    "The actual daemon resource-serving route preserves MIME and network isolation.");
                Encoding.UTF8.GetBytes(html["text"]!.GetValue<string>()).ShouldBe(fixtureBytes,
                    "Exact owned file bytes are served; this is NOT assembled consumer HTML/vendor qualification.");
                // Three build facts stay in consumer tests/mcp-details-browser.mjs at d81feac9ca21aef2ea5606c592695943a04bb3dc.
                Check((await Rpc("resources/read", new JsonObject { ["uri"] = "file:///private" })).Body["error"]!["code"]!
                    .GetValue<int>() == -32002, "Arbitrary local resources must not be readable through MCP.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(true), owner: true)).Status == HttpStatusCode.OK,
                    "Use the fixture's actual scoped read admission.");
                var page = await Call("read_bench", new JsonObject { ["limit"] = 100 });
                var members = page["structuredContent"]!;
                Check(members["total"]!.GetValue<int>() == 2 && members["items"]!.AsArray().Count == 2
                    && members["items"]!.AsArray().All(member => JsonNode.DeepEquals(member!["sourceId"], source["sourceId"]))
                    && members["items"]!.AsArray().Select(member => member!["itemId"]!.GetValue<string>()).Order()
                        .SequenceEqual(source["items"]!.AsArray().Select(member => member!["id"]!.GetValue<string>()).Order())
                    && members["items"]!.AsArray().Any(member => member!["itemId"]!.GetValue<string>() == item["id"]!.GetValue<string>()),
                    "MCP item discovery returns the actual source-qualified member.");
                JsonObject Details() => new()
                {
                    ["snapshotId"] = members["snapshotId"]!.DeepClone(), ["sourceId"] = source["sourceId"]!.DeepClone(),
                    ["itemId"] = item["id"]!.DeepClone(), ["fields"] = new JsonArray("System.Description"),
                    ["focusField"] = "System.Description", ["note"] = "Note the observed description.",
                };
                var details = await Call("show_work_item_details", Details());
                Check(details["isError"] is null, details.ToJsonString());
                var data = details["structuredContent"]!;
                var observedTitle = item["cachedFields"]!.AsArray().Single(field =>
                    field!["reference"]!.GetValue<string>() == "System.Title")!["value"]!["value"]!.GetValue<string>();
                Check(data["title"]!.GetValue<string>() == observedTitle
                    && data["itemId"]!.GetValue<string>() == item["id"]!.GetValue<string>()
                    && data["readOnly"]!.GetValue<bool>() && data["revision"]!.GetValue<string>().Length > 0
                    && data["fields"]!.AsArray().Count == 1 && data["cacheReadAt"] is not null,
                    "Details are daemon-observed values, exact revision and provenance, with only requested fields.");
                data["revision"]!.GetValue<string>().ShouldBe(
                    "ado-rev:" + _transport.RevisionOf(42).ToString(System.Globalization.CultureInfo.InvariantCulture));
                data["snapshotId"]!.GetValue<Guid>().ShouldBe(members["snapshotId"]!.GetValue<Guid>());
                data["sourceId"]!.GetValue<Guid>().ShouldBe(source["sourceId"]!.GetValue<Guid>());
                data["connection"]!.ToJsonString().ShouldBe(source["connection"]!.ToJsonString());
                data["sourceLabel"]!.GetValue<string>().ShouldBe(source["label"]!.GetValue<string>());
                data["mode"]!.GetValue<string>().ShouldBe(members["sources"]![0]!["mode"]!.GetValue<string>());
                var requestedField = data["fields"]![0]!;
                var observedField = item["cachedFields"]!.AsArray().Single(field =>
                    field!["reference"]!.GetValue<string>() == "System.Description")!;
                Check(requestedField["reference"]!.GetValue<string>() == "System.Description"
                    && requestedField["observed"]!.GetValue<bool>()
                    && JsonNode.DeepEquals(requestedField["value"], observedField["value"])
                    && data["focusField"]!.GetValue<string>() == "System.Description"
                    && data["note"]!.GetValue<string>() == "Note the observed description."
                    && data["cacheReadAt"]!.GetValue<DateTimeOffset>() <= DateTimeOffset.UtcNow,
                    "Requested field content and observation time come from the real native cache, not model input.");
                var observedType = source["types"]!.AsArray().Single(type =>
                    type!["reference"]!.GetValue<string>() == item["type"]!.GetValue<string>())!;
                Check(JsonNode.DeepEquals(data["webUrl"], item["webUrl"])
                    && JsonNode.DeepEquals(data["typePresentation"]?["name"], observedType["presentation"]?["name"])
                    && JsonNode.DeepEquals(data["typePresentation"]?["color"], observedType["presentation"]?["color"])
                    && data["assignedProfiles"]!.AsArray().Count == 0,
                    "Links and type presentation come from observed item/schema; no unselected identity lookup.");
                Check(gateway.ProfileReads == 0, "Unselected identities must not trigger provider profile reads.");
                var profileRequest = Details(); profileRequest["assignedToField"] = "System.AssignedTo";
                var profiled = await Call("show_work_item_details", profileRequest);
                Check(profiled["isError"] is null, profiled.ToJsonString());
                var identity = item["cachedFields"]!.AsArray().Single(field =>
                    field!["reference"]!.GetValue<string>() == "System.AssignedTo")!["value"]!["value"]!.GetValue<string>();
                Check(gateway.ProfileReads == 1 && gateway.ProfileIdentities.SequenceEqual([identity])
                    && profiled["structuredContent"]!["assignedProfiles"]![0]!["id"]!.GetValue<string>() == identity
                    && profiled["structuredContent"]!["assignedProfiles"]![0]!["displayName"]!.GetValue<string>() == "Synthetic Full Name"
                    && profiled["structuredContent"]!["assignedProfiles"]![0]!["error"]!.GetValue<string>() == "Synthetic photo unavailable",
                    "Only the cached assignment's exact identity is resolved through the bound native source; profile gaps stay explicit.");
                var wrongAssignee = Details(); wrongAssignee["assignedToField"] = "System.Title";
                Check((await Call("show_work_item_details", wrongAssignee))["isError"]!.GetValue<bool>()
                    && gateway.ProfileReads == 1, "A text field must not become an assignee lookup.");
                var fabricated = Details(); fabricated["title"] = "Invented title";
                Check((await Call("show_work_item_details", fabricated))["isError"]!.GetValue<bool>(),
                    "The model cannot supply or override item values.");
                var unknown = Details(); unknown["fields"] = new JsonArray("Not.A.Field"); unknown["focusField"] = "Not.A.Field";
                Check((await Call("show_work_item_details", unknown))["isError"]!.GetValue<bool>(),
                    "Field selection is checked against the observed item's actual schema.");
                var wrong = Details(); wrong["itemId"] = new string('0', 64);
                Check((await Call("show_work_item_details", wrong))["isError"]!.GetValue<bool>(),
                    "Unobserved or out-of-Bench items cannot be fetched through MCP.");
                Check((await Rpc("tools/call", new JsonObject { ["name"] = "apply", ["arguments"] = new JsonObject() }))
                    .Body["error"]!["code"]!.GetValue<int>() == -32602, "No MCP mutation tool is exposed.");
                var newer = await Call("read_bench", new JsonObject());
                Check(newer["structuredContent"]!["snapshotId"]!.GetValue<Guid>() != members["snapshotId"]!.GetValue<Guid>(),
                    "Reading again captures a distinct observation.");
                Check((await Call("show_work_item_details", Details()))["isError"]!.GetValue<bool>(),
                    "A replaced snapshot must not silently fetch or replay details.");
                Check((await Send("/agent/permissions", HttpMethod.Put, Grant(false), owner: true)).Status == HttpStatusCode.OK,
                    "Explicit owner restrictions remain supported.");
                Check((await Rpc("tools/call", new JsonObject { ["name"] = "read_bench", ["arguments"] = new JsonObject() }))
                    .Status == HttpStatusCode.Forbidden, "MCP cannot bypass an explicit owner restriction.");
            }
        }
        finally
        {
            HumanActorDenied = false;
            sessions.Revoke(ownerSession);
            await app.StopAsync(CancellationToken.None);
        }

        static void Check(bool value, string message) => value.ShouldBeTrue(message);
    }

    private sealed class AgentTestTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
