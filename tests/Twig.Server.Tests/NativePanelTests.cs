using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;

namespace Twig.Server.Tests.Native;

internal static partial class NativePanelTests
{
    private sealed class SavedAccountGateway : INativeConnectionSetupGateway
    {
        public Task<Result<NativeAadEnrollment[]>> ListAadIdentitiesAsync(CancellationToken ct)
            => Task.FromResult(Result<NativeAadEnrollment[]>.Ok([new("saved", "saved-identity",
                "11111111-2222-4333-8444-555555555555", "object", "issuer", "authority", "fixture@example.net")]));
        public Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request,
            Action<Uri> authorizationUrl, CancellationToken ct) => throw new InvalidOperationException("No sign-in during listing.");
        public Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request, CancellationToken ct)
            => throw new InvalidOperationException("No binding during listing.");
        public Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request, CancellationToken ct)
            => throw new InvalidOperationException("No initialization during listing.");
    }

    internal static async Task Run(bool profilesOnly = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-native-panel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (!profilesOnly) { await ConnectionAdmission(directory); await PresentationRevocation(directory); }
            var store = new UserBenchStore(Path.Combine(directory, "benches.db"));
            var cli = new UserBenchStore(Path.Combine(directory, "benches.db"));
            var connection = new Connection(new("ado"), "11111111-2222-4333-8444-555555555555",
                "66666666-7777-4888-8999-aaaaaaaaaaaa", """["binding","saved-identity","aad","actor"]""");
            await new TwigConfiguration { Organization = "Example organization", Project = "Example project", Provider = "ado" }
                .SaveAsync(Path.Combine(directory, "twig.json"));
            var other = new Connection(new("github"), "synthetic-api", "synthetic-repo", "synthetic-identity");
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
            var foreign = new Selector(SelectorKind.Pin, ItemKey.Remote(other.Provider, other.ResourceScope, "I_node"), connection: other);
            var bench = Required(await cli.SaveAsync(new(Guid.NewGuid(), "Shared",
                [new(SelectorKind.Pin, key, connection: connection), foreign]), 0));
            var local = ItemKey.Local(Guid.NewGuid());
            var gateway = new Gateway(connection, key, local, store);
            var changes = new CatalogChanges();
            var adapter = new NativeSourceAdapter(Path.Combine(directory, "evidence"), gateway, changes);
            var panel = new NativePanel(store, gateway, adapter);
            var shared = Required(await store.ReadAsync());
            await ObserveWorkspace(Path.Combine(directory, "benches.db"), directory, connection,
                Required(await gateway.ReadAsync(directory, null, false, default)).Source.StoreInstance);
            var sessions = new ViewSessions(Path.Combine(directory, "presentation"));
            var registration = sessions.RegisterNativeCanvas(shared.InstanceId);
            var paired = sessions.ConnectOwner(shared.InstanceId, registration.ViewId, Guid.NewGuid());
            await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes, new SavedAccountGateway());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(int Status, JsonNode? Body)> Send(string method, string route, JsonNode? body = null, string? bearer = null)
            {
                using var request = new HttpRequestMessage(new(method), route);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? paired.ContentCapability);
                if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
            }
            var prefix = $"/api/v1/views/{paired.ViewId:D}";
            Check((await Send("GET", prefix + "/benches", bearer: registration.PresentationCapability)).Status == 403,
                "SDK presentation capability must not read native definitions.");
            Check((await Send("GET", prefix + "/events", bearer: registration.PresentationCapability)).Status == 403,
                "SDK presentation capability must not subscribe to human state.");
            Check((await Send("GET", prefix + "/benches")).Body!["benches"]!.AsArray().Count == 1, "Native CLI definitions must appear.");
            gateway.ReadRoots.Clear();
            var labelList = await Send("GET", prefix + "/benches");
            Check(labelList.Status == 200 && labelList.Body!["workspaces"]!.AsArray().Count == 1 && gateway.ReadRoots.Count == 0,
                "Populated connection labels must not trigger native gateway or provider acquisition.");
            var wireConnection = labelList.Body!["workspaces"]![0]!.AsObject();
            Check(!wireConnection["displayName"]!.GetValue<string>().Contains(connection.ResourceScope, StringComparison.Ordinal)
                && !wireConnection["displayName"]!.GetValue<string>().Contains(connection.Destination, StringComparison.Ordinal),
                "Human connection labels must not use resource-scope or destination GUIDs.");
            Check(wireConnection.Select(field => field.Key).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "account", "available", "connection", "displayName", "labelAvailable", "provider", "root" })
                && wireConnection["resourceScope"] is null && wireConnection["destination"] is null,
                "The current DTO separates availability and canonical connection identity from human labels.");
            Check(wireConnection["account"]!["name"]!.GetValue<string>() == "fixture@example.net"
                && wireConnection["account"]!["kind"]!.GetValue<string>() == "orgid"
                && wireConnection["account"]!.AsObject().Count == 2,
                "Connections expose only the exact attached account's display name and type, without credential material.");
            Check((await Send("POST", prefix + "/sources", new JsonObject())).Status == 404, "Legacy registration must not exist in production.");
            var noConnection = await Send("PUT", prefix + "/population-context", new JsonObject { ["root"] = null });
            Check(noConnection.Status == 409 && noConnection.Body!["message"]!.GetValue<string>().Contains("connection", StringComparison.OrdinalIgnoreCase),
                "Use connection must not succeed without any actual selected connection.");
            gateway.Denied = true;
            var deniedConnection = await Send("PUT", prefix + "/population-context", new JsonObject { ["root"] = directory });
            Check(deniedConnection.Status == 403 && deniedConnection.Body!["code"]!.GetValue<string>() == "ProviderAccessDenied"
                && deniedConnection.Body!["message"]!.GetValue<string>().Contains("connection", StringComparison.OrdinalIgnoreCase)
                && !deniedConnection.Body["message"]!.GetValue<string>().Contains("UUID", StringComparison.Ordinal),
                "The original Use connection endpoint must report actionable native admission, not publication-UUID recovery.");
            gateway.Denied = false;
            Check((await Send("PUT", prefix + "/context", new JsonObject { ["root"] = directory })).Status == 200, "Explicit native context admission.");
            var profileSource = Required(await panel.OpenAsync(directory, default)).Source.Id;
            var profileBody = new JsonObject { ["sourceId"] = profileSource.ToString("D"), ["identities"] = new JsonArray("synthetic-actor") };
            Check((await Send("POST", prefix + "/profiles", profileBody, registration.PresentationCapability)).Status == 403,
                "SDK presentation capability must not read identity names or photos.");
            Check((await Send("POST", prefix + "/profiles", new JsonObject { ["sourceId"] = Guid.NewGuid().ToString("D"),
                ["identities"] = new JsonArray("synthetic-actor") })).Status == 409, "Profiles require an admitted source in this view.");
            Check((await Send("POST", prefix + "/profiles", new JsonObject { ["sourceId"] = profileSource.ToString("D"),
                ["identities"] = new JsonArray("synthetic-actor", "synthetic-actor") })).Status == 422, "Profile batches reject duplicate identities.");
            var profileResponse = await Send("POST", prefix + "/profiles", profileBody);
            Check(profileResponse.Status == 200 && profileResponse.Body![0]!["id"]!.GetValue<string>() == "synthetic-actor"
                && profileResponse.Body[0]!["displayName"]!.GetValue<string>() == "Synthetic Full Name"
                && profileResponse.Body[0]!["imageDataUrl"]!.GetValue<string>().StartsWith("data:image/png;base64,", StringComparison.Ordinal),
                "Authenticated profiles preserve exact identities and deliver local photos with resolved names.");
            gateway.ProfilePhotosUnavailable = true;
            var unavailablePhoto = await Send("POST", prefix + "/profiles", profileBody);
            Check(unavailablePhoto.Status == 200 && unavailablePhoto.Body![0]!["imageDataUrl"] is null
                && unavailablePhoto.Body[0]!["error"]!.GetValue<string>() == "Synthetic photo unavailable"
                && (await Send("GET", prefix + "/drafts")).Status == 200,
                "An unavailable optional photo preserves its name and cannot poison work-item content admission.");
            gateway.ProfilePhotosUnavailable = false;
            if (profilesOnly)
            {
                gateway.AuthorityChanged = true;
                Check((await Send("POST", prefix + "/profiles", profileBody)).Status == 409,
                    "Changed native authority cannot release identity profiles.");
                gateway.AuthorityChanged = false;
                Check((await Send("POST", prefix + "/profiles", profileBody)).Status == 403,
                    "A failed authority check must remain blocked until explicit successful source reauthorization.");
                Console.WriteLine("PASS native profiles: exact source admission, SDK refusal, bounded batches, local photos, optional-photo fallback and authority-change refusal");
                return;
            }
            var labeled = (await Send("GET", prefix + "/benches")).Body!["contextConnection"]!;
            var renderedLabel = labeled["displayName"]!.GetValue<string>();
            Check(labeled["labelAvailable"]!.GetValue<bool>() && renderedLabel.Contains("Example project", StringComparison.Ordinal)
                && !renderedLabel.Contains(connection.Destination, StringComparison.Ordinal)
                && !renderedLabel.Contains(connection.ResourceScope, StringComparison.Ordinal),
                "Presentation DTOs must use explicit local human names, not immutable GUID identity fields.");
            gateway.ReadConflict = true;
            var readConflict = await Send("GET", prefix + "/drafts");
            var conflictMessage = readConflict.Body!["message"]!.GetValue<string>();
            Check(readConflict.Status == 409 && readConflict.Body["code"]!.GetValue<string>() == "SourceChanged"
                && conflictMessage.Contains("Refresh", StringComparison.Ordinal)
                && conflictMessage.Contains("no changes", StringComparison.OrdinalIgnoreCase)
                && !conflictMessage.Contains("account", StringComparison.OrdinalIgnoreCase)
                && !conflictMessage.Contains("network", StringComparison.OrdinalIgnoreCase),
                "A known read Conflict must request a fresh read of saved Bench/context changes, not imply account or network denial.");
            gateway.ReadConflict = false;
            Check((await Send("POST", prefix + "/context/recheck", new JsonObject())).Status == 200,
                "The test restores this captured context only through its explicit native recheck.");
            Check((await Send("POST", prefix + "/attachment", new JsonObject())).Status == 200,
                "A healthy admitted source may explicitly attach presentation controls.");
            Check((await Send("PUT", prefix + "/layout", new JsonObject { ["layout"] = "list" },
                registration.PresentationCapability)).Status == 200, "The positive presentation grant must work before denial.");
            Check((await Send("PUT", prefix + "/population-context", new JsonObject { ["root"] = directory })).Status == 200,
                "Population/planning denial coverage requires its own explicitly selected native source.");
            gateway.PolicyDenied = true;
            var concurrentRead = Send("GET", prefix + "/drafts");
            var concurrentLayout = Send("PUT", prefix + "/layout", new JsonObject { ["layout"] = "list" }, registration.PresentationCapability);
            await Task.WhenAll(concurrentRead, concurrentLayout);
            Check(concurrentRead.Result.Status == 403 && concurrentLayout.Result.Status is 200 or 403,
                "A concurrent ACK may precede denial, but the read must fail closed.");
            if (concurrentLayout.Result.Status == 200)
                Check(concurrentLayout.Result.Body!.AsObject().Count == 1
                    && concurrentLayout.Result.Body["acknowledged"]!.GetValue<bool>(), "Concurrent SDK ACK must remain content-free.");
            Check((await Send("GET", prefix + "/session")).Status == 200,
                "Bench selection metadata must remain readable independently of a denied provider.");
            foreach (var route in new[] { "/drafts", "/picker", "/planning" })
            {
                var deniedRead = await Send("GET", prefix + route);
                var deniedMessage = deniedRead.Body!["message"]!.GetValue<string>();
                Check(deniedRead.Status == 403 && deniedRead.Body["code"]!.GetValue<string>() == "PolicyViolation"
                    && deniedMessage.Contains("connection", StringComparison.OrdinalIgnoreCase)
                    && deniedMessage.Contains("selected", StringComparison.Ordinal)
                    && !deniedMessage.Contains("UUID", StringComparison.Ordinal),
                    "Admitted-context connection reads must preserve PolicyViolation with reauthorization guidance, never mutation UUID recovery.");
            }
            Check((await Send("PUT", prefix + "/layout", new JsonObject { ["layout"] = "tree" },
                registration.PresentationCapability)).Status == 403,
                "Known source denial must not retain previously attached SDK layout authority.");
            foreach (var route in new[] { "/planning/sources", "/planning/refresh", "/planning/browse", "/attachment" })
            {
                var body = route.EndsWith("/browse", StringComparison.Ordinal)
                    ? new JsonObject { ["kind"] = 0, ["id"] = Gateway.QueryId, ["team"] = Gateway.TeamA } : new JsonObject();
                var deniedRead = await Send("POST", prefix + route, body);
                var message = deniedRead.Body!["message"]!.GetValue<string>();
                Check(deniedRead.Status == 403 && deniedRead.Body["code"]!.GetValue<string>() == "PolicyViolation"
                    && message.Contains("selected", StringComparison.Ordinal) && !message.Contains("UUID", StringComparison.Ordinal),
                    "Explicit native source/planning reads must retain connection-denial recovery, not mutation advice.");
            }
            gateway.PolicyDenied = false;
            var interrupted = await Send("GET", prefix + "/drafts");
            Check(interrupted.Status == 403, "Removing the synthetic refusal must not clear an observed source denial.");
            gateway.AuthorityChanged = true;
            Check((await Send("POST", prefix + "/context/recheck", new JsonObject())).Status == 409,
                "Re-admission must reject authority replacement instead of choosing a new source.");
            gateway.AuthorityChanged = false;
            Check((await Send("POST", prefix + "/context/recheck", new JsonObject())).Status == 200,
                "Explicit schema refresh stays on the captured native root and authority.");
            // Start the remaining independent route fixtures with a fresh source.
            gateway.ReplaceStore();
            Check((await Send("PUT", prefix + "/context", new JsonObject { ["root"] = directory })).Status == 200,
                "Independent route fixtures use a new synthetic store, not restored denied history.");
            var fresh = sessions.RegisterNativeCanvas(shared.InstanceId);
            var freshOwner = sessions.ConnectOwner(shared.InstanceId, fresh.ViewId, Guid.NewGuid());
            Check((await Send("GET", $"/api/v1/views/{freshOwner.ViewId:D}/session", bearer: freshOwner.ContentCapability)).Body!["preferences"]!["benchId"] is null
                && (await Send("PUT", $"/api/v1/views/{freshOwner.ViewId:D}/layout", new JsonObject { ["layout"] = "list" },
                    fresh.PresentationCapability)).Status == 403, "A fresh owner view must not inherit stale source selection or presenter attachment.");
            Check((await Send("PUT", prefix + "/selection", new JsonObject { ["expectedBenchId"] = null, ["benchId"] = bench.Id.ToString("D") })).Status == 200,
                "Bench selection is an explicit native context operation.");
            var otherRoot = Path.Combine(directory, "population context");
            Directory.CreateDirectory(otherRoot);
            Check((await Send("PUT", prefix + "/population-context", new JsonObject { ["root"] = otherRoot })).Status == 200,
                "Choose an independent population connection.");
            gateway.ReadRoots.Clear();
            Check((await Send("GET", prefix + "/picker")).Status == 200 && gateway.ReadRoots.Count > 0 && gateway.ReadRoots.All(root => root == otherRoot),
                "The picker reads the explicitly selected population context.");
            Check((await Send("GET", prefix + "/session")).Body!["preferences"]!["benchId"]!.GetValue<string>() == bench.Id.ToString("D"),
                "Population source choice must not switch the view's CLI selection.");
            Check((await Send("PUT", prefix + "/population-context", new JsonObject { ["root"] = directory })).Status == 200,
                "Explicitly return population to the viewing connection without relying on an implicit fallback.");
            var view = await Send("GET", prefix + $"/benches/{bench.Id:D}");
            Check(view.Status == 200, "Native Bench projection.");
            Check(view.Body!["sources"]![0]!["label"]!.GetValue<string>().Contains("Example project", StringComparison.Ordinal)
                && !view.Body["sources"]![0]!["label"]!.GetValue<string>().Contains(directory, StringComparison.Ordinal),
                "Primary connection identity must be provider/account/project, never an absolute workspace root.");
            Check(view.Body!["sources"]!.AsArray().Count == 2 && view.Body["sources"]!.AsArray().Count(source => source!["errorCode"] is not null) == 1,
                "A saved but unchosen connection must remain explicit unknown membership in the actual HTTP view, not disappear from the Bench.");
            var rows = view.Body!["sources"]![0]!["items"]!.AsArray();
            Check(rows.Count == 2, "Excluded published draft and local seed must both remain visible.");
            Check(rows.Any(row => row!["isExcluded"]!.GetValue<bool>() && row["hasDraft"]!.GetValue<bool>()), "Excluded protected draft debt.");
            Check(rows.Single(row => row!["isLocal"]!.GetValue<bool>())!["webUrl"] is null, "A local seed must have no fabricated web URL.");
            var teams = await Send("GET", prefix + "/planning/teams");
            Check(teams.Status == 200 && teams.Body!.AsArray().Count == 2, "Native canonical team discovery.");
            var planning = await Send("POST", prefix + "/planning/refresh", new JsonObject { ["team"] = Gateway.TeamA });
            Check(planning.Status == 200 && planning.Body!["metadata"]!["team"]!.GetValue<string>() == Gateway.TeamA,
                "Explicit planning refresh must retain the exact native team.");
            var rule = await Send("POST", prefix + "/planning/rule", new JsonObject
            { ["area"] = "Synthetic\\Area", ["includeChildren"] = false, ["iteration"] = "@current+1", ["assignedToMe"] = true, ["team"] = Gateway.TeamA });
            Check(rule.Status == 200, "Native planning rule construction.");
            var choices = await Send("POST", prefix + "/planning/sources", new JsonObject { ["team"] = Gateway.TeamA });
            Check(choices.Status == 200 && choices.Body!["entries"]!.AsArray().Count == 2, "Native query/backlog discovery.");
            var acquired = await Send("POST", prefix + "/planning/browse", new JsonObject
            { ["kind"] = 0, ["id"] = Gateway.QueryId, ["team"] = Gateway.TeamA });
            Check(acquired.Status == 200 && acquired.Body!["picker"]!["source"]!["items"]!.AsArray().Count == 1,
                "The freshly acquired work item remains findable even if excluded from the workspace.");
            var browseSource = acquired.Body!["picker"]!["source"]!;
            Check(browseSource["edges"]!.AsArray().Count == 0
                && browseSource["unresolvedReferences"]!.AsArray().Count == 0,
                "Browse must not emit unrelated cached hierarchy endpoints.");
            Check(browseSource["items"]![0]!["title"]!.GetValue<string>() == "Fresh planning result",
                "Browse must render provider-returned fields rather than cached fields or draft overlays.");
            Check(!browseSource["items"]![0]!["relationshipsComplete"]!.GetValue<bool>(),
                "The result must not claim complete relationships after omitting an out-of-page endpoint.");
            var reference = BenchCodec.DeserializeSelectors(acquired.Body!["selectors"]!.GetValue<string>()).Single();
            Check(reference.Query!.Kind == QueryKind.SavedQuery && reference.Query.Value!.Text == Gateway.QueryId
                && reference.Query.PlanningTeam == Gateway.TeamA && reference.Item is null, "Pin native definition identity, not current item results.");
            var selectors = bench.Selectors.AddRange(BenchCodec.DeserializeSelectors(rule.Body!["selectors"]!.GetValue<string>())).Add(reference);
            var renamed = await Send("PUT", prefix + $"/benches/{bench.Id:D}", new JsonObject
            { ["name"] = "Renamed in panel", ["expectedRevision"] = bench.Revision, ["selectors"] = BenchCodec.SerializeSelectors(selectors) });
            Check(renamed.Status == 200, "Panel edit commits to native definition.");
            var observed = Required(await cli.ReadAsync()).Benches.Single();
            Check(observed.Name == "Renamed in panel" && observed.Selectors.Contains(foreign), "CLI sees edit; other connection pins preserved.");
            Check(observed.Selectors.Single(selector => selector.Query?.Kind == QueryKind.And).Query!.Children.Any(query =>
                query.Kind == QueryKind.Iteration && query.PlanningTeam == Gateway.TeamA && query.Value!.Values.Contains("@Current+1")),
                "Planning pins store canonical rule and team intent, not result IDs.");
            var secondCalendar = await Send("POST", prefix + "/planning/refresh", new JsonObject { ["team"] = Gateway.TeamB });
            Check(secondCalendar.Status == 200 && secondCalendar.Body!["metadata"]!["team"]!.GetValue<string>() == Gateway.TeamB,
                "The Add items team reaches native planning discovery.");
            var firstCalendar = await Send("POST", prefix + "/planning/read", new JsonObject { ["team"] = Gateway.TeamA });
            Check(firstCalendar.Status == 200 && firstCalendar.Body!["metadata"]!["team"]!.GetValue<string>() == Gateway.TeamA
                && firstCalendar.Body["metadata"]!["iterations"]![0]!["path"]!.GetValue<string>() == "Synthetic\\Current",
                "Refreshing another team must not replace the first team's calendar.");
            var secondRule = await Send("POST", prefix + "/planning/rule", new JsonObject
            { ["area"] = null, ["includeChildren"] = false, ["iteration"] = "@Current", ["assignedToMe"] = false, ["team"] = Gateway.TeamB });
            Check(secondRule.Status == 200 && BenchCodec.DeserializeSelectors(secondRule.Body!["selectors"]!.GetValue<string>())
                .Single().Query!.PlanningTeam == Gateway.TeamB, "A rule binds the add-time team, never the last configured calendar.");
            var projectCalendar = await Send("POST", prefix + "/planning/read", new JsonObject { ["team"] = null });
            Check(projectCalendar.Status == 200 && projectCalendar.Body!["metadata"] is null,
                "Explicit project scope must not fall back to either selected team.");
            Check((await Send("GET", prefix + "/picker")).Status == 200,
                "An uncached optional planning read must not poison the admitted source.");
            var ownershipOnly = await Send("POST", prefix + "/planning/rule", new JsonObject
            { ["area"] = null, ["includeChildren"] = false, ["iteration"] = null, ["assignedToMe"] = true, ["team"] = null });
            Check(ownershipOnly.Status == 200, "An ownership-only rule does not require a planning calendar.");
            var missingCalendar = await Send("POST", prefix + "/planning/rule", new JsonObject
            { ["area"] = null, ["includeChildren"] = false, ["iteration"] = "@Current", ["assignedToMe"] = false, ["team"] = null });
            Check(missingCalendar.Status == 409 && (await Send("GET", prefix + "/picker")).Status == 200,
                "A missing relative calendar is an actionable rule prerequisite, not source revocation.");
            Check(BenchCodec.SerializeSelectors(Required(await cli.ReadAsync()).Benches.Single().Selectors)
                == BenchCodec.SerializeSelectors(observed.Selectors),
                "Planning discovery does not rewrite existing connection/team-qualified rule bytes.");
            Check((await Send("DELETE", prefix + $"/benches/{bench.Id:D}",
                new JsonObject { ["expectedRevision"] = bench.Revision })).Status == 409, "Stale edits must fail.");
            gateway.Denied = true;
            var deniedView = await Send("GET", prefix + $"/benches/{bench.Id:D}");
            Check(deniedView.Status == 200 && deniedView.Body!["sources"]!.AsArray()
                .All(source => source!["errorCode"] is not null && source["items"]!.AsArray().Count == 0),
                "A fresh provider denial must remove its content while retaining the degraded Bench and other source statuses.");
            gateway.Denied = false;
            Check((await Send("GET", prefix + "/drafts")).Status == 403,
                "A successful connection probe must not restore denied work-item history.");
            Check((await Send("GET", prefix + $"/benches/{bench.Id:D}")).Status == 200,
                "Bench metadata remains available while work-item history is withheld.");
            Check((await Send("DELETE", prefix + $"/benches/{bench.Id:D}",
                new JsonObject { ["expectedRevision"] = observed.Revision })).Status == 200, "Deleting a definition.");
            Check(Required(await cli.ReadAsync()).Benches.IsEmpty, "Bench deletion removes only the native shared definition.");
            var largeId = Guid.NewGuid();
            var largeSelectors = Enumerable.Range(1, 300).Select(number => new Selector(SelectorKind.Pin,
                ItemKey.Remote(connection.Provider, connection.ResourceScope, number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                connection: connection));
            var large = new JsonObject { ["name"] = "Native-sized definition", ["expectedRevision"] = 0,
                ["selectors"] = BenchCodec.SerializeSelectors(largeSelectors) };
            Check(Encoding.UTF8.GetByteCount(large.ToJsonString()) > 16384, "The regression must cross the obsolete prototype request bound.");
            Check((await Send("PUT", prefix + $"/benches/{largeId:D}", large)).Status == 200,
                "The HTTP transport must preserve supported native-sized definitions, not cap them at the prototype's limit.");
            Check(Required(await cli.ReadAsync()).Benches.Single().Selectors.Length == 300, "Large native pin definitions round trip without truncation.");
            Check((await Send("DELETE", prefix + $"/benches/{largeId:D}", new JsonObject { ["expectedRevision"] = 1 })).Status == 200,
                "Removing only the large test definition.");
            await app.StopAsync();
            Console.WriteLine("PASS native production HTTP authority, SDK denial, shared edits, exclusions, stale CAS and protected drafts");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task PresentationRevocation(string parent)
    {
        var root = Path.Combine(parent, "presentation-revocation");
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var gateway = new NativeSourceGateway();
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, new()));
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        var instance = Required(await store.ReadAsync()).InstanceId;
        var registration = sessions.RegisterNativeCanvas(instance);
        var owner = sessions.ConnectOwner(instance, registration.ViewId, Guid.NewGuid());
        sessions.AttachPresentation(owner.ViewId, owner.ContentCapability);
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        foreach (var removed in new[] { "/index.html", "/device.js", "/api/v1/device-enrollment" })
            Check((await client.GetAsync(removed)).StatusCode == HttpStatusCode.NotFound, "Removed prototype route: " + removed);
        async Task<HttpStatusCode> Send(HttpMethod method, string suffix, string capability)
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/views/{owner.ViewId:D}" + suffix);
            request.Headers.Add("X-Twig-Bench", "1");
            request.Headers.Authorization = new("Bearer", capability);
            if (method == HttpMethod.Put) request.Content = new StringContent("{\"layout\":\"tree\"}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        Check(await Send(HttpMethod.Put, "/layout", registration.PresentationCapability) == HttpStatusCode.OK,
            "Current attached SDK grant must work before revocation.");
        Check(await Send(HttpMethod.Delete, "/session", owner.ContentCapability) == HttpStatusCode.OK,
            "Current native session endpoint must confirm revocation.");
        Check(await Send(HttpMethod.Put, "/layout", registration.PresentationCapability) == HttpStatusCode.Forbidden
            && await Send(HttpMethod.Get, "/session", owner.ContentCapability) == HttpStatusCode.Forbidden,
            "Confirmed revocation must reject both stale SDK layout and human content capabilities.");
        await app.StopAsync();
        Console.WriteLine("PASS current native HTTP presentation grant, confirmed revocation and stale SDK/content rejection");
    }

    private static async Task ObserveWorkspace(string database, string root, Connection connection, Guid storeInstance)
    {
        await using var db = new SqliteConnection("Data Source=" + database + ";Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO workspaces(context_key,root,connection,store_instance) VALUES($key,$root,$connection,$instance)";
        command.Parameters.AddWithValue("$key", root);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$connection", JsonNode.Parse(BenchCodec.SerializeSelectors(
            [new(SelectorKind.Query, query: new(QueryKind.All), connection: connection)]))![0]!["Connection"]!.ToJsonString());
        command.Parameters.AddWithValue("$instance", storeInstance.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ConnectionAdmission(string parent)
    {
        var root = Path.Combine(parent, "real uninitialized native context");
        Directory.CreateDirectory(root);
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var gateway = new NativeSourceGateway();
        var changes = new CatalogChanges();
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        var instance = Required(await store.ReadAsync()).InstanceId;
        var owner = sessions.ConnectOwner(instance, null, Guid.NewGuid());
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        using var body = new StringContent(new JsonObject { ["root"] = root }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"/api/v1/views/{owner.ViewId:D}/population-context", body);
        var value = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var message = value["message"]!.GetValue<string>();
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable
            && value["code"]!.GetValue<string>() == "StorageUnavailable"
            && message.Contains("Twig CLI", StringComparison.Ordinal) && message.Contains("connection", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("UUID", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(root, ".twig")),
            "The original connection endpoint must exercise real native admission, explain the missing initialized connection and never initialize or expose publication recovery.");
        await app.StopAsync();
        Console.WriteLine("PASS real native connection admission refuses uninitialized context with actionable recovery and no publication UUID");
    }

    private sealed class Gateway(Connection connection, ItemKey key, ItemKey local, UserBenchStore store) : INativeSourceGateway
    {
        internal const string QueryId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        internal const string TeamA = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        internal const string TeamB = "bbbbbbbb-aaaa-4ccc-8ddd-eeeeeeeeeeee";
        public bool Denied { get; set; }
        internal bool ReadConflict { get; set; }
        internal bool PolicyDenied { get; set; }
        internal bool AuthorityChanged { get; set; }
        internal TimeSpan OnlineDelay { get; set; }
        internal int OnlineReads { get; private set; }
        internal int SchemaReads { get; private set; }
        internal bool SchemaUnavailable { get; set; }
        internal bool DiagnosticFixture { get; init; }
        internal bool ProcessFixture { get; init; }
        internal bool MissingProcessMetadata { get; set; }
        internal bool ProcessChanged { get; set; }
        internal int DiagnosticRevision { get; set; } = 1;
        internal int DiagnosticCount { get; set; } = 111;
        private ImmutableArray<string> DiagnosticMessages => DiagnosticFixture
            ? Enumerable.Range(1, DiagnosticCount).Select(index =>
                $"SYNTHETIC_DIAGNOSTIC_{index:D3} revision {DiagnosticRevision}: unsupported native process metadata should remain in local logs, never above the work table. " +
                """{"authorization":"Bearer SYNTHETIC_SECRET_MUST_NOT_LOG","value":"SYNTHETIC_ITEM_CONTENT_MUST_NOT_LOG"}""").ToImmutableArray()
            : [];
        internal List<string> ReadRoots { get; } = [];
        private PlanningMetadata? planning;
        private readonly Dictionary<string, PlanningMetadata> calendars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NativePlanningSourcesCache> catalogues = new(StringComparer.Ordinal);
        private readonly List<PlanningMembership> memberships = [];
        private static PlanningSources Choices => new(TeamA,
            [new(PlanningSourceKind.SavedQuery, QueryId, "Synthetic query", "Shared/Synthetic query", QueryType: "flat"),
                new(PlanningSourceKind.Backlog, "Synthetic.Backlog", "Synthetic backlog", "Synthetic backlog", RequiresTeam: true)], []);
        private NativeSourceStamp stamp = new(connection, Guid.NewGuid(), "synthetic-actor", "synthetic-authority");
        internal void ReplaceStore() => stamp = stamp with { StoreInstance = Guid.NewGuid() };
        internal bool ProfilePhotosUnavailable { get; set; }
        public async Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string root, NativeSourceStamp expected,
            ImmutableArray<string> identities, CancellationToken ct)
        {
            var observed = await ReadOnlineAsync(root, expected, ct);
            return observed.IsSuccess ? Result<NativeSourceObservation>.Ok(observed.Value with {
                IdentityProfiles = identities.Select(id => new IdentityProfile(id, "Synthetic Full Name",
                    ProfilePhotosUnavailable ? null : "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=",
                    ProfilePhotosUnavailable ? "Synthetic photo unavailable" : null)).ToImmutableArray()
            }) : observed;
        }
        public async Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        {
            OnlineReads++;
            if (OnlineDelay > TimeSpan.Zero) await Task.Delay(OnlineDelay, ct);
            return await ReadAsync(root, expected, false, ct);
        }
        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        {
            ReadRoots.Add(root);
            if (observeSchema)
            {
                SchemaReads++;
                if (SchemaUnavailable) return Task.FromResult(Result<NativeSourceObservation>.Fail(
                    ErrorCode.Unavailable, "Synthetic schema discovery unavailable."));
            }
            if (ReadConflict) return Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic definition/context changed during read."));
            if (PolicyDenied) return Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.PolicyViolation, "Synthetic native selection-read denial."));
            if (AuthorityChanged && expected is not null) return Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic native authority changed."));
            if (Denied) return Task.FromResult(Result<NativeSourceObservation>.Fail(new Error(ErrorCode.Unavailable, "Synthetic native denial") { Evidence = ["http:403"] }));
            if (expected is not null && expected != stamp) return Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic changed authority"));
            var fields = FieldValues.Empty.Set("System.Title", FieldValue.String("Synthetic task"))
                .Set("System.AreaPath", FieldValue.String("Synthetic\\Area"))
                .Set("System.IterationPath", FieldValue.String("Synthetic\\Current"))
                .Set("System.AssignedTo", FieldValue.Identity("synthetic-actor"));
            var type = new ItemType("Synthetic.Task", "System.State",
                [new("System.Title", FieldKind.Text), new("System.AreaPath", FieldKind.Text),
                    new("System.IterationPath", FieldKind.Text), new("System.AssignedTo", FieldKind.Identity),
                    new("System.Description", FieldKind.Text)], [], [], []);
            var item = new WorkItem(key, type.Reference, fields, new(key, "1"), true);
            var items = new List<WorkItem> { item };
            if (DiagnosticFixture) foreach (var number in Enumerable.Range(43, 2))
            {
                var extra = ItemKey.Remote(connection.Provider, connection.ResourceScope, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                items.Add(new(extra, type.Reference, fields.Set("System.Title", FieldValue.String("Synthetic cached item " + number)), new(extra, "1"), true));
            }
            ItemType[] types = [type];
            if (ProcessFixture)
            {
                var unit = new ItemType(ProcessChanged ? "Orion.Decision" : "Nebula.CustomUnit", "Custom.UnitPhase",
                    type.Fields.Append(new("Custom.UnitPhase", FieldKind.Text, Name: "Decision phase")),
                    [new("Waiting for quorum", StateCategory.InProgress, "7ECAF5")], [], [],
                    presentation: new("Decision unit", "DA7C3A", "custom_observed_prism", IconSvg:
                        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#DA7C3A" d="M8 1 15 14H1Z"/></svg>"""));
                var signal = new ItemType(ProcessChanged ? "Orion.Signal" : "Nebula.SignalUnit", "Custom.SignalPhase",
                    type.Fields.Append(new("Custom.SignalPhase", FieldKind.Text, Name: "Signal phase")),
                    [new("Waiting for quorum", StateCategory.InProgress, "F8BD78")], [], [],
                    presentation: new("Signal unit", "50C7A7", "custom_observed_diamond", IconSvg:
                        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#50C7A7" d="m8 1 7 7-7 7-7-7Z"/></svg>"""));
                types = MissingProcessMetadata ? [unit] : [unit, signal];
                items = items.Select((current, index) =>
                {
                    var observedType = index == 1 ? signal : unit;
                    var observedFields = current.Fields.Set(observedType.StateField,
                        FieldValue.String(index == 2 ? "Not in roster" : "Waiting for quorum"));
                    return new WorkItem(current.Key, observedType.Reference, observedFields, current.Revision, true);
                }).ToList();
            }
            var drafts = DiagnosticFixture ? [] : new[] { new Draft(key, type.Reference, item, fields), new Draft(local, type.Reference, null, fields) };
            var relationships = DiagnosticFixture ? Array.Empty<Relationship>()
                : new[] { new Relationship(key, local, "Synthetic.Hierarchy", RelationshipOrigin.Local) };
            return Task.FromResult(Result<NativeSourceObservation>.Ok(new(stamp, new(1, items, relationships, drafts),
                DateTimeOffset.UtcNow, observeSchema ? Result<ProcessSchema>.Ok(new(connection, types,
                    [new("Synthetic.Hierarchy", RelationShape.Hierarchy, true)], DiagnosticMessages, webUrl: "https://dev.azure.com/synthetic/project/")
                    { Planning = planning, PlanningCalendars = calendars.Values.ToImmutableArray(), Memberships = memberships.ToImmutableArray() }) : null,
                observeSchema ? DateTimeOffset.UtcNow : null) { WorkspaceExclusions = DiagnosticFixture ? [] : [new(SelectorKind.Pin, key, connection: connection)],
                    Planning = planning,
                    PlanningCalendars = calendars.Values.ToImmutableArray(), Memberships = memberships.ToImmutableArray() }));
        }
        public Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            planning = new(now, ["Synthetic\\Area"],
                [new("Synthetic\\Current", now.AddDays(-1), now.AddDays(1)), new("Synthetic\\Next", now.AddDays(2), now.AddDays(3))],
                DiagnosticMessages, TeamA);
            calendars[TeamA] = planning;
            return ReadAsync(root, expected, true, ct);
        }
        public async Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        {
            var admitted = await ReadAsync(root, expected, false, ct);
            return admitted.IsSuccess ? Result<ImmutableArray<PlanningTeam>>.Ok([new(TeamA, "Example team"), new(TeamB, "Another team")])
                : Result<ImmutableArray<PlanningTeam>>.Fail(admitted.Error);
        }
        public async Task<Result<NativeSourceObservation>> ReadPlanningAsync(string root, NativeSourceStamp expected, string? team, CancellationToken ct)
        {
            var observed = await ReadAsync(root, expected, false, ct);
            if (!observed.IsSuccess) return observed;
            var calendar = calendars.GetValueOrDefault(team ?? "");
            return calendar is null ? Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "This explicit planning scope has no retained calendar; refresh it explicitly.")
                : Result<NativeSourceObservation>.Ok(observed.Value with { Planning = calendar });
        }
        public async Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string root, NativeSourceStamp expected, string? team, CancellationToken ct)
        {
            var admitted = await ReadAsync(root, expected, false, ct);
            if (!admitted.IsSuccess) return admitted;
            if (team is not null && team != TeamA && team != TeamB)
                return Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Choose an available canonical team.");
            var now = DateTimeOffset.UtcNow;
            var metadata = new PlanningMetadata(now, ["Synthetic\\Area", "Synthetic\\Area\\Child"],
                [new(team == TeamB ? "Synthetic\\OtherCurrent" : "Synthetic\\Current", now.AddDays(-1), now.AddDays(1), team is not null),
                    new(team == TeamB ? "Synthetic\\OtherNext" : "Synthetic\\Next", now.AddDays(2), now.AddDays(3), team is not null)], DiagnosticMessages, team);
            calendars[team ?? ""] = metadata;
            var saved = await store.SavePlanningAsync(connection, team ?? "", metadata, ct);
            if (!saved.IsSuccess) return Result<NativeSourceObservation>.Fail(saved.Error);
            var observed = await ReadAsync(root, expected, true, ct);
            if (!observed.IsSuccess) return observed;
            var schema = observed.Value.Schema ?? throw new InvalidOperationException("The fixture must return its real native schema.");
            return Result<NativeSourceObservation>.Ok(observed.Value with
            { Planning = metadata, Schema = Result<ProcessSchema>.Ok(schema.Value with { Planning = metadata }) });
        }
        public async Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string root, NativeSourceStamp expected, string? team, CancellationToken ct)
        {
            var observed = await ReadAsync(root, expected, false, ct);
            if (!observed.IsSuccess) return observed;
            var choices = Choices with { Team = team, Entries = team is null ? Choices.Entries.Where(value => !value.RequiresTeam).ToImmutableArray() : Choices.Entries };
            var cache = new NativePlanningSourcesCache(choices, DateTimeOffset.UtcNow);
            catalogues[team ?? ""] = cache;
            return Result<NativeSourceObservation>.Ok(observed.Value with { PlanningSources = choices, PlanningSourcesCache = cache });
        }
        public async Task<Result<NativeSourceObservation>> ReadPlanningSourcesAsync(string root, NativeSourceStamp expected, string? team, CancellationToken ct)
        {
            var observed = await ReadAsync(root, expected, false, ct);
            if (!observed.IsSuccess) return observed;
            var cache = catalogues.GetValueOrDefault(team ?? "") ?? new NativePlanningSourcesCache(null, null);
            return Result<NativeSourceObservation>.Ok(observed.Value with { PlanningSources = cache.Sources, PlanningSourcesCache = cache });
        }
        public Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
            string? team, PlanningSourceKind kind, string id, ProcessSchema mappingSchema, CancellationToken ct)
            => mappingSchema.Connection == connection ? BrowsePlanningAsync(root, expected, team, kind, id, ct)
                : Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "The mapping schema belongs to another connection."));
        public async Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
            string? team, PlanningSourceKind kind, string id, CancellationToken ct)
        {
            var observed = await DiscoverPlanningSourcesAsync(root, expected, team, ct);
            if (!observed.IsSuccess) return observed;
            var source = observed.Value.PlanningSources!.Entries.SingleOrDefault(value => value.Kind == kind && value.Id == id);
            if (source is null) return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Choose an available source for this team.");
            var now = DateTimeOffset.UtcNow;
            memberships.RemoveAll(value => value.Kind == kind && value.Id == id && value.Team == team);
            memberships.Add(new(kind, id, team, [key], now, true));
            observed = await ReadAsync(root, expected, true, ct);
            var cached = observed.Value.Snapshot.Items.Single(item => item.Key == key);
            var fresh = new WorkItem(key, cached.Type, cached.Fields.Set("System.Title", FieldValue.String("Fresh planning result")),
                cached.Revision, cached.RelationshipsComplete);
            var outside = ItemKey.Remote(connection.Provider, connection.ResourceScope, "99");
            return Result<NativeSourceObservation>.Ok(observed.Value with { PlanningBrowse = new(source, [key], now, team),
                AcquiredPlanningPage = new([fresh], [new(key, outside, "Synthetic.Hierarchy", RelationshipOrigin.Provider)], null, true) });
        }
        public async Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        {
            var observed = await ReadAsync(root, expected, true, ct);
            return observed.IsSuccess ? Result<NativeSourceObservation>.Ok(observed.Value with
                { PlanningSources = Choices }) : observed;
        }
        public async Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
            PlanningSourceKind kind, string id, string? team, CancellationToken ct)
        {
            var observed = await ReadAsync(root, expected, true, ct);
            if (!observed.IsSuccess) return observed;
            var source = Choices.Entries.SingleOrDefault(source => source.Kind == kind && source.Id == id);
            if (source is null || team != Choices.Team) return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic native source or team changed.");
            var now = DateTimeOffset.UtcNow;
            memberships.RemoveAll(value => value.Kind == kind && value.Id == id);
            memberships.Add(new(kind, id, team, [key], now, true));
            observed = await ReadAsync(root, expected, true, ct);
            return Result<NativeSourceObservation>.Ok(observed.Value with { PlanningBrowse = new(source, [key], now, team),
                AcquiredPlanningPage = new([observed.Value.Snapshot.Items.Single(item => item.Key == key)], [], null, true) });
        }
    }
}
