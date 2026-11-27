using System.Collections.Immutable;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using GitHub = Twig.Providers.GitHub.GitHubProvider;

namespace Twig.Server.Tests.Native;

internal static class NativeAggregationTests
{
    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-native-aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = new Connection(new("ado"), "collection", "project", "ado-actor");
            var b = new Connection(new("github"), "api", "repo", "gh-actor");
            var keyA = ItemKey.Remote(a.Provider, a.ResourceScope, "42");
            var keyB = ItemKey.Remote(b.Provider, b.ResourceScope, "I_42");
            var seed = ItemKey.Local(Guid.NewGuid());
            var aRoot = Path.Combine(root, "anchor");
            var bRoot = Path.Combine(root, "chosen population");
            var otherRoot = Path.Combine(root, "unchosen sibling");
            Directory.CreateDirectory(aRoot); Directory.CreateDirectory(bRoot); Directory.CreateDirectory(otherRoot);
            var fieldsA = FieldValues.Empty.Set("System.Title", FieldValue.String("Synthetic A"))
                .Set("System.AssignedTo", FieldValue.Identity("synthetic-actor"));
            var fieldsB = FieldValues.Empty.Set(GitHub.Title, FieldValue.String("Synthetic B")).Set(GitHub.Number, FieldValue.Integer(42));
            var itemA = new WorkItem(keyA, "Native.Task", fieldsA, new(keyA, "1"), true);
            var itemB = new WorkItem(keyB, "Native.Issue", fieldsB, new(keyB, "1"), true);
            var gateway = new Gateway(new Dictionary<string, NativeSourceObservation>(StringComparer.Ordinal)
            {
                [aRoot] = Observation(a, new(1, [itemA], retainedItems: [new LocalEdits(keyA, itemA.Type, itemA, fieldsA)]), "https://example.test/project/"),
                [bRoot] = Observation(b, new(1, [itemB]), "https://github.com/synthetic/repo/"),
                [otherRoot] = Observation(a, new(1, retainedItems: [new Seed(seed, itemA.Type, fieldsA)]), "https://example.test/project/"),
            });
            var store = new UserBenchStore(Path.Combine(root, "benches.db"));
            var changes = new CatalogChanges();
            var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes));
            var bench = Required(await store.SaveAsync(new(Guid.NewGuid(), "Cross-connection intent",
                [new(SelectorKind.Pin, keyA, connection: a), new(SelectorKind.Pin, keyB, connection: b)]), 0));
            var anchor = Required(await panel.OpenAsync(aRoot, default));
            gateway.Reads.Clear();
            var missing = Required(await panel.ReadViewAsync(bench.Id, anchor, [], default));
            Check(missing.View.Sources.Length == 2 && missing.View.Sources.Count(source => source.Error is not null) == 1,
                "Unchosen connection membership must be explicit, not omitted or represented as empty.");
            Check(gateway.Reads.All(path => path == aRoot), "No registry/sibling worktree fallback is allowed.");
            var chosen = Required(await panel.OpenAsync(bRoot, default));
            var complete = Required(await panel.ReadViewAsync(bench.Id, anchor, [chosen], default));
            Check(complete.View.Sources.All(source => source.Error is null) && complete.View.Sources.Sum(source => source.Items.Count(item => item.BenchMember)) == 2,
                "Both explicitly admitted native connections must contribute their pinned members.");
            Check(complete.View.Sources.SelectMany(source => source.Items).Select(item => item.Identity).Distinct().Count() == 2,
                "Equal human display numbers in distinct native sources must not merge identities.");
            await HttpView(root, panel, changes, bench.Id, aRoot, bRoot);
            var mappedSeed = ItemKey.Local(Guid.NewGuid());
            var mappedChanges = fieldsA.Set("System.Title", FieldValue.String("Retained mapped seed draft"));
            var plan = new Plan(a, "schema", 1, [new("create", OpKind.Create, mappedSeed, fields: fieldsA,
                type: itemA.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
            var applying = Required(JournalEntry.Planned(plan.Digest, 0).Begin(Guid.NewGuid()));
            var applied = Required(applying.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Applied, ["accepted"], itemA.Revision, keyA)));
            var verified = Required(applied.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Verified, ["read back"], itemA.Revision, keyA)));
            bench = Required(await store.SaveAsync(new(bench.Id, bench.Name,
                [new(SelectorKind.Pin, mappedSeed, connection: a), new(SelectorKind.Pin, keyB, connection: b)]), bench.Revision));
            gateway.Replace(aRoot, Observation(a, new(2, [itemA],
                retainedItems: [new Seed(mappedSeed, itemA.Type, mappedChanges)],
                plans: [plan], journal: [verified], mappings: [new(mappedSeed, keyA, plan.Digest, 0)]), "https://example.test/project/"));
            var mapped = Required(await panel.ReadViewAsync(bench.Id, anchor, [chosen], default));
            var mappedRow = mapped.View.Sources.Single(source => source.SourceId == anchor.Source.Id).Items.Single();
            Check(mappedRow.Identity.Key == keyA && mappedRow.BenchMember && mappedRow.HasDraft
                && mappedRow.EffectiveFields["System.Title"].Text == "Retained mapped seed draft",
                "A local UUID pin must resolve through this context's permanent mapping without dropping its retained draft.");
            gateway.Exclude(aRoot, new(SelectorKind.Pin, mappedSeed, connection: a));
            var excludedMapped = Required(await panel.ReadViewAsync(bench.Id, anchor, [chosen], default));
            mappedRow = excludedMapped.View.Sources.Single(source => source.SourceId == anchor.Source.Id).Items.Single();
            Check(mappedRow.Identity.Key == keyA && mappedRow.BenchMember && mappedRow.HasDraft && mappedRow.IsExcluded,
                "A mapped UUID workspace exclusion must protect the remote-identified draft outside the view without changing canonical membership.");
            await HttpView(root, panel, changes, bench.Id, aRoot, bRoot, expectExcludedDraft: true);
            Check(Required(await store.ReadAsync()).Benches.Single().Selectors.Any(selector => selector.Item == mappedSeed),
                "Permanent mapping or exclusion evaluation must never rewrite the persisted local UUID selector.");
            gateway.Denied.Add(bRoot);
            var partial = Required(await panel.ReadViewAsync(bench.Id, anchor, [chosen], default));
            Check(partial.View.Sources.Single(source => source.SourceId == chosen.Source.Id).Items.IsEmpty
                && partial.View.Sources.Single(source => source.SourceId == chosen.Source.Id).Error is not null
                && partial.View.Sources.Single(source => source.SourceId == anchor.Source.Id).Items.Any(item => item.HasDraft),
                "One denied connection must never release its cache or hide another admitted context's protected drafts.");
            bench = Required(await store.SaveAsync(new(bench.Id, bench.Name,
                bench.Selectors.Add(new(SelectorKind.Pin, seed, connection: a))), bench.Revision));
            var sibling = Required(await panel.OpenAsync(otherRoot, default));
            gateway.Reads.Clear();
            var unavailableSeed = Required(await panel.ReadViewAsync(bench.Id, anchor, [sibling], default));
            var anchored = unavailableSeed.View.Sources.Single(source => source.SourceId == anchor.Source.Id);
            Check(anchored.Error is not null && anchored.Items.Any(item => item.HasDraft && !item.BenchMember),
                "An elsewhere-owned pinned local UUID must stay unavailable while admitted anchor draft debt remains visible.");
            Check(!gateway.Reads.Contains(otherRoot), "A chosen population sibling must not silently replace the anchor's same-connection item mirror.");
            Check(Required(await store.ReadAsync()).Benches.Single().Selectors.Any(selector => selector.Item == seed),
                "Unavailable local UUID intent must never be dropped or rewritten.");
            gateway.Denied.Clear();
            gateway.ReplaceStore(bRoot);
            chosen = Required(await panel.OpenAsync(bRoot, default));
            bench = Required(await store.SaveAsync(new(bench.Id, bench.Name,
                [new(SelectorKind.Pin, keyA, connection: a), new(SelectorKind.Pin, keyB, connection: b)]), bench.Revision));
            gateway.Replace(aRoot, Observation(a, new(3, [itemA], [
                new(keyA, keyB, "related", RelationshipOrigin.Provider),
                new(keyA, ItemKey.Remote(b.Provider, b.ResourceScope, "I_uncached"), "related", RelationshipOrigin.Provider)
            ]), "https://example.test/project/"));
            await AutomaticView(root, panel, changes, bench.Id, anchor, chosen, gateway);
            gateway.ReplaceStore(aRoot);
            gateway.ReplaceStore(bRoot);
            anchor = Required(await panel.OpenAsync(aRoot, default));
            chosen = Required(await panel.OpenAsync(bRoot, default));
            await using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "benches.db") + ";Pooling=False"))
            {
                await db.OpenAsync();
                foreach (var source in new[] { anchor, chosen })
                {
                    await using var command = db.CreateCommand();
                    command.CommandText = "UPDATE workspaces SET store_instance=$instance WHERE root=$root";
                    command.Parameters.AddWithValue("$instance", source.Stamp.StoreInstance.ToString("D"));
                    command.Parameters.AddWithValue("$root", source.Root);
                    Check(await command.ExecuteNonQueryAsync() == 1, "Register the replacement test store for cold resolution.");
                }
            }
            var restricted = Required(await panel.ReadViewAsync(bench.Id, anchor, [chosen], default, available: new HashSet<Connection> { a }));
            Check(restricted.View.Sources.Single(source => source.RequiredConnection == b).Error?.Code == ErrorCode.PolicyViolation
                && restricted.View.Sources.Single(source => source.RequiredConnection == b).Items.IsEmpty
                && restricted.View.Sources.Single(source => source.RequiredConnection == a).Items.Any(item => item.BenchMember),
                "A repository's unavailable dependency remains explicit without hiding healthy membership or borrowing its cache. "
                    + string.Join("; ", restricted.View.Sources.Select(source => $"{source.RequiredConnection?.Provider.Value}:{source.Error?.Code}:{source.Error?.Message}:{source.Items.Count(item => item.BenchMember)}")));
            gateway.Block(aRoot, bRoot);
            var resolving = panel.ResolveConnectionsAsync(bench.Id, [], default);
            try { await gateway.LaterRead.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { gateway.Release.TrySetResult(); }
            Check(Required(await resolving).Length == 2, "Cold source resolution must start a later healthy source independently of a stalled first source.");
            gateway.Block(aRoot, bRoot);
            var reading = panel.ReadViewAsync(bench.Id, anchor, [chosen], default);
            try { await gateway.LaterRead.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { gateway.Release.TrySetResult(); }
            Check(Required(await reading).View.Sources.All(source => source.Error is null),
                "A later healthy source must be acquired independently of a stalled first source during view projection.");
            await RepositoryView(root, panel, changes, bench.Id, a, b, gateway);
            Console.WriteLine("PASS native multi-connection Bench aggregation, explicit contexts, mapped UUID/exclusion HTTP preservation, source identity, scoped denial and unavailable-local-UUID draft protection");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static NativeSourceObservation Observation(Connection connection, StoreSnapshot snapshot, string url)
        => new(new(connection, Guid.NewGuid(), "synthetic-actor", Guid.NewGuid().ToString("N")), snapshot, DateTimeOffset.UtcNow,
            Result<ProcessSchema>.Ok(new(connection, connection.Provider.Value == "ado"
                ? [new ItemType("Native.Task", "System.State",
                    [new("System.AssignedTo", FieldKind.Identity), new("System.Title", FieldKind.Text),
                        new("System.State", FieldKind.Text)], [], [], [])]
                : [], [], webUrl: url)), DateTimeOffset.UtcNow);
    private static async Task AutomaticView(string root, NativePanel panel, CatalogChanges changes, Guid benchId,
        NativePanelContext a, NativePanelContext b, Gateway gateway)
    {
        await using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "benches.db") + ";Pooling=False"))
        {
            await db.OpenAsync();
            foreach (var source in new[] { a, b })
            {
                await using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO workspaces(context_key,root,connection,store_instance) VALUES($root,$root,$connection,$instance)";
                command.Parameters.AddWithValue("$root", source.Root);
                command.Parameters.AddWithValue("$connection", JsonNode.Parse(BenchCodec.SerializeSelectors(
                    [new(SelectorKind.Query, query: new(QueryKind.All), connection: source.Stamp.Connection)]))![0]!["Connection"]!.ToJsonString());
                command.Parameters.AddWithValue("$instance", source.Stamp.StoreInstance.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
        }
        var sessions = new ViewSessions(Path.Combine(root, "automatic-presentation"));
        var instance = Required(await panel.ListAsync(default)).InstanceId;
        var registration = sessions.RegisterNativeCanvas(instance);
        var owner = sessions.ConnectOwner(instance, registration.ViewId, Guid.NewGuid());
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        var prefix = "/api/v1/views/" + owner.ViewId.ToString("D");
        using (var body = new StringContent(new JsonObject { ["expectedBenchId"] = null, ["benchId"] = benchId }.ToJsonString(), Encoding.UTF8, "application/json"))
        using (var selected = await client.PutAsync(prefix + "/selection", body))
            Check(selected.IsSuccessStatusCode, "Selecting a Bench must not require choosing or using a connection.");
        async Task<JsonNode[]> Read()
        {
            using var response = await client.GetAsync(prefix + "/benches/" + benchId);
            var value = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(response.IsSuccessStatusCode, "A degraded Bench remains readable: " + value);
            return value["sources"]!.AsArray().Select(value => value!).ToArray();
        }
        var complete = await Read();
        Check(complete.Length == 2 && complete.All(source => source["errorCode"] is null)
            && complete.Sum(source => source["items"]!.AsArray().Count) == 2,
            "The Bench must resolve both exact registered connections without any context endpoint.");
        var sourceA = complete.Single(source => source["provider"]!.GetValue<string>() == "ado");
        var sourceB = complete.Single(source => source["provider"]!.GetValue<string>() == "github");
        var references = sourceA["unresolvedReferences"]!.AsArray();
        Check(references.Count == 1 && references[0]!["nativeId"]!.GetValue<string>() == "I_uncached"
            && references[0]!["provider"]!.GetValue<string>() == "github"
            && references[0]!["connections"]!.AsArray().Count == 1
            && references[0]!["title"] is null && references[0]!["type"] is null && references[0]!["fields"] is null,
            "An uncached target is identity metadata, never a fabricated WorkItem.");
        Check(sourceA["edges"]!.AsArray().Any(edge => edge!["to"]!.GetValue<string>()
            == sourceB["items"]![0]!["id"]!.GetValue<string>()),
            "An observed cross-source endpoint resolves to its exact visible item.");
        using (var body = new StringContent(new JsonObject
            { ["benchId"] = benchId, ["read"] = true, ["draft"] = false, ["membership"] = false }.ToJsonString(),
            Encoding.UTF8, "application/json"))
        using (var permitted = await client.PutAsync(prefix + "/agent/permissions", body))
            Check(permitted.IsSuccessStatusCode, "The fixture must admit scoped reads before testing MCP degradation.");
        async Task<JsonNode> Mcp(string tool, JsonObject arguments)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/mcp");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.PresentationCapability);
            request.Content = new StringContent(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
            }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var value = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(response.IsSuccessStatusCode && value["result"]?["isError"] is null,
                "A missing unrelated source must not block a healthy MCP read: " + value);
            return value["result"]!["structuredContent"]!;
        }
        // Capture complete membership first; an exact detail from A can still be
        // observed if unrelated B becomes unavailable after the capture.
        var mcpBench = await Mcp("read_bench", new JsonObject());
        gateway.Denied.Add(b.Root);
        var degraded = await Read();
        sourceA = degraded.Single(source => source["provider"]!.GetValue<string>() == "ado");
        Check(sourceA["items"]!.AsArray().Count == 1
            && degraded.Count(source => source["errorCode"] is not null) == 1
            && sourceA["unresolvedReferences"]!.AsArray().Count == 2
            && sourceA["unresolvedReferences"]!.AsArray().All(reference => reference!["reason"]!.GetValue<string>() == "ConnectionUnavailable"),
            "Unavailable targets retain both links and source metadata without hiding healthy items or releasing denied cache.");
        var details = await Mcp("show_work_item_details", new JsonObject
        {
            ["snapshotId"] = mcpBench["snapshotId"]!.DeepClone(), ["sourceId"] = sourceA["sourceId"]!.DeepClone(),
            ["itemId"] = sourceA["items"]![0]!["id"]!.DeepClone(), ["fields"] = new JsonArray(),
            ["assignedToField"] = "System.AssignedTo"
        });
        Check(details["unresolvedReferences"]!.AsArray().Count == 0 && details["readOnly"]!.GetValue<bool>(),
            "Delegated details must not add unresolved references from the historical snapshot.");
        Check(details["assignedProfiles"]!.AsArray().Single()!["id"]!.GetValue<string>() == "synthetic-actor",
            "A healthy source's assignee remains readable when an unrelated source is denied.");
        gateway.Denied.Add(a.Root);
        Check((await Read()).All(source => source["errorCode"] is not null),
            "All unavailable connections still yield an explicit degraded Bench, not failed Bench selection or empty success.");
        using (var metadata = await client.GetAsync(prefix + "/session"))
            Check(metadata.IsSuccessStatusCode, "Provider failures must not hide Bench selection metadata.");
        var empty = Required(await panel.SaveAsync(new(Guid.NewGuid(), "Empty independent Bench", []), 0, default));
        using (var body = new StringContent(new JsonObject { ["expectedBenchId"] = benchId, ["benchId"] = empty.Id }.ToJsonString(), Encoding.UTF8, "application/json"))
        using (var selected = await client.PutAsync(prefix + "/selection", body))
            Check(selected.IsSuccessStatusCode, "An empty Bench is selectable without any healthy connection.");
        using (var response = await client.GetAsync(prefix + "/benches/" + empty.Id))
            Check(response.IsSuccessStatusCode
                && JsonNode.Parse(await response.Content.ReadAsStringAsync())!["sources"]!.AsArray().Count == 0,
                "Empty Bench intent does not acquire an arbitrary anchor source.");
        await app.StopAsync();
        gateway.Denied.Clear();
        Console.WriteLine("PASS Bench-first automatic registered connections, partial/all-source degradation and metadata-only cross-source references");
    }
    private static async Task RepositoryView(string root, NativePanel panel, CatalogChanges changes, Guid benchId, Connection a, Connection b, Gateway gateway)
    {
        var repo = Path.Combine(root, "code");
        Directory.CreateDirectory(repo);
        var start = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init"); start.ArgumentList.Add("--quiet");
        using (var git = System.Diagnostics.Process.Start(start)!)
        {
            await git.WaitForExitAsync();
            Check(git.ExitCode == 0, "The repository-context fixture must initialize successfully.");
        }
        var bindings = Required(await panel.SaveRepositoryAsync(repo, 0, [a], benchId, default, [benchId]));
        var sessions = new ViewSessions(Path.Combine(root, "session-profiles"));
        var instance = Required(await panel.ListAsync(default)).InstanceId;
        var globalProfile = sessions.ResolveOwnerProfile(instance, new(null, null));
        var first = sessions.RegisterNativeCanvas(instance, new("session-one", repo, Repository: "synthetic/repo"));
        var firstProfile = sessions.ResolveOwnerProfile(instance, new(null, null, first.ViewId));
        var second = sessions.RegisterNativeCanvas(instance, new("session-two", repo));
        var secondProfile = sessions.ResolveOwnerProfile(instance, new(null, null, second.ViewId));
        var reopened = sessions.RegisterNativeCanvas(instance, new("session-one", repo));
        Check(firstProfile.ProfileId != secondProfile.ProfileId
            && sessions.ResolveOwnerProfile(instance, new(null, null, reopened.ViewId)).ProfileId == firstProfile.ProfileId,
            "Session profiles must remain stable within one real SDK session and isolated across sessions.");
        var owner = sessions.ConnectOwner(instance, first.ViewId, firstProfile.ProfileId);
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        var prefix = "/api/v1/views/" + first.ViewId.ToString("D");
        using (var body = new StringContent(new JsonObject { ["context"] = new JsonObject
            { ["sessionId"] = "remote-session", ["workingDirectory"] = repo, ["isRemote"] = true } }.ToJsonString(),
            Encoding.UTF8, "application/json"))
        using (var remote = await client.PostAsync("/api/v1/canvas-views", body))
            Check(remote.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity,
                "The server itself must refuse remote context instead of allocating a local fallback profile.");
        async Task<(System.Net.HttpStatusCode Status, JsonNode Body)> Request(string path, HttpMethod? method = null, JsonObject? body = null, string? capability = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, prefix + path);
            if (capability is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
        }
        var restored = await Request("/view-state/restore", HttpMethod.Post, new());
        Check(restored.Status == System.Net.HttpStatusCode.OK, "Runtime repository selection must initialize owner state: " + restored.Body);
        var state = await Request("/view-state");
        Check(state.Status == System.Net.HttpStatusCode.OK
            && state.Body["currentBenchId"]?.GetValue<Guid>() == benchId
            && state.Body["runtimeContext"]?["sessionId"]?.GetValue<string>() == "session-one",
            "The actual SDK session repository must select its user-local default Bench, not another session's choice.");
        Check(sessions.ResolveOwnerProfile(instance, new(null, null)).ProfileId == globalProfile.ProfileId,
            "A contextual session must not overwrite the retained global owner profile.");
        var view = await Request("/benches/" + benchId);
        Check(view.Status == System.Net.HttpStatusCode.OK && view.Body["sources"]!.AsArray().Count == 2
            && view.Body["sources"]!.AsArray().Count(source => source!["errorCode"] is not null) == 1,
            "A repo-disallowed Bench dependency must remain visibly degraded over actual HTTP.");
        var picker = await Request("/picker");
        Check(picker.Status == System.Net.HttpStatusCode.Conflict && picker.Body["code"]!.GetValue<string>() == "DestinationRequired",
            "A degraded multi-source Bench cannot silently use its only healthy source as the Add items destination.");
        var unavailableRoot = Required(await panel.WorkspacesAsync(default)).Single(workspace => workspace.Connection == b).Root;
        gateway.Reads.Clear();
        var deniedContext = await Request("/context", HttpMethod.Put, new() { ["root"] = unavailableRoot });
        Check(deniedContext.Status == System.Net.HttpStatusCode.Forbidden && !gateway.Reads.Contains(unavailableRoot),
            "Repository-unavailable context choices must refuse before reading or admitting the source.");
        var forbidden = await Request("/repository-bindings?root=" + Uri.EscapeDataString(repo), capability: first.PresentationCapability);
        Check(forbidden.Status == System.Net.HttpStatusCode.Forbidden, "Agent presentation authority cannot edit or read owner repository management routes.");
        var selected = Required(await panel.ListAsync(default)).Benches.Single(value => value.Id == benchId);
        var empty = Required(await panel.SaveAsync(new(Guid.NewGuid(), "Attach without membership", []), 0, default));
        var attached = await Request("/benches/" + empty.Id + "/connections", HttpMethod.Post,
            new() { ["expectedRevision"] = empty.Revision, ["connection"] = System.Text.Json.JsonSerializer.SerializeToNode(a, TransportJson.Default.Connection) });
        Check(attached.Status == System.Net.HttpStatusCode.OK, "An available registered connection can be explicitly attached: " + attached.Body);
        var definition = Required(await panel.ListAsync(default)).Benches.Single(value => value.Id == empty.Id);
        Check(definition.Connections.SequenceEqual([a]) && definition.Selectors.IsEmpty,
            "Attaching an existing source must never fabricate a membership rule.");
        var permissions = await Request("/agent/permissions", HttpMethod.Put,
            new() { ["benchId"] = benchId, ["read"] = true, ["draft"] = false, ["membership"] = false });
        Check(permissions.Status == System.Net.HttpStatusCode.OK, "The fixture must grant scoped reads before a repository rebind.");
        var aware = await Request("/agent/context", capability: first.PresentationCapability);
        Check(aware.Status == System.Net.HttpStatusCode.OK
            && aware.Body["runtimeContext"]!["sessionId"]!.GetValue<string>() == "session-one"
            && aware.Body["repositoryRoot"]!.GetValue<string>() == repo
            && aware.Body["repositoryRevision"]!.GetValue<long>() == bindings.Revision,
            "The current agent envelope always includes actual SDK metadata and the exact repository binding revision without negotiation.");
        Required(await panel.SaveRepositoryAsync(repo, bindings.Revision, [b], benchId, default));
        _ = await Request("/session");
        var grant = await Request("/agent/context", capability: first.PresentationCapability);
        Check(grant.Status != System.Net.HttpStatusCode.OK, "A repository binding revision must invalidate the old scoped agent grant.");
        Check(Required(await panel.ListAsync(default)).Benches.Single(value => value.Id == benchId).Revision == selected.Revision,
            "Repository rebinding must not rewrite a shared Bench's rules, dependencies or draft ownership.");
        await app.StopAsync();
    }

    private static async Task HttpView(string root, NativePanel panel, CatalogChanges changes, Guid benchId, string anchor, string population,
        bool expectExcludedDraft = false)
    {
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        var instance = Required(await panel.ListAsync(default)).InstanceId;
        var registration = sessions.RegisterNativeCanvas(instance);
        var owner = sessions.ConnectOwner(instance, registration.ViewId, Guid.NewGuid());
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        var prefix = "/api/v1/views/" + owner.ViewId.ToString("D");
        async Task Put(string route, string path)
        {
            using var body = new StringContent(new JsonObject { ["root"] = path }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(prefix + route, body);
            Check(response.IsSuccessStatusCode, "Actual HTTP context choice failed.");
        }
        await Put("/context", anchor);
        using (var response = await client.GetAsync(prefix + "/benches/" + benchId.ToString("D")))
        {
            var value = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(response.IsSuccessStatusCode && value["sources"]!.AsArray().Count(source => source!["errorCode"] is not null) == 1,
                "Actual HTTP must retain an unchosen connection as unknown membership.");
        }
        await Put("/population-context", population);
        using (var response = await client.GetAsync(prefix + "/benches/" + benchId.ToString("D")))
        {
            var value = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var contexts = value["sources"]!.AsArray();
            Check(response.IsSuccessStatusCode && contexts.Count == 2 && contexts.All(source => source!["errorCode"] is null),
                "Actual HTTP must display both explicitly chosen connections.");
            var rows = contexts.SelectMany(source => source!["items"]!.AsArray()).ToArray();
            Check(rows.Select(row => row!["id"]!.GetValue<string>()).Distinct().Count() == 2
                && rows.All(row => row!["locator"]!.GetValue<string>() == "42")
                && rows.Single(row => row!["webUrl"]!.GetValue<string>().Contains("github.com", StringComparison.Ordinal))!["webUrl"]!
                    .GetValue<string>() == "https://github.com/synthetic/repo/issues/42",
                "Source-qualified identities and native published links must survive actual HTTP aggregation.");
            if (expectExcludedDraft)
            {
                var mapped = contexts.Single(source => source!["provider"]!.GetValue<string>() == "ado")!["items"]!.AsArray().Single()!;
                Check(mapped["isLocal"]!.GetValue<bool>() == false && mapped["hasDraft"]!.GetValue<bool>()
                    && mapped["benchMember"]!.GetValue<bool>() && mapped["isExcluded"]!.GetValue<bool>()
                    && mapped["title"]!.GetValue<string>() == "Retained mapped seed draft"
                    && mapped["webUrl"]!.GetValue<string>() == "https://example.test/project/_workitems/edit/42",
                    "Actual aggregated HTTP must retain mapped draft debt, exclusion state, proposed fields and the mapped native source link.");
            }
        }
        await app.StopAsync();
    }
    private static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Gateway(Dictionary<string, NativeSourceObservation> observations) : INativeSourceGateway
    {
        public async Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string root, NativeSourceStamp expected,
            System.Collections.Immutable.ImmutableArray<string> identities, CancellationToken ct)
        {
            var read = await ReadAsync(root, expected, false, ct);
            return read.IsSuccess ? Result<NativeSourceObservation>.Ok(read.Value with {
                IdentityProfiles = [.. identities.Select(id => new IdentityProfile(id, "Synthetic actor", null, null))]
            }) : read;
        }
        public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
            => ReadAsync(root, expected, false, ct);
        public async Task<Result<System.Collections.Immutable.ImmutableArray<WorkItem>>> ObserveItemsAsync(
            string root, NativeSourceStamp expected, System.Collections.Immutable.ImmutableArray<ItemKey> keys, CancellationToken ct)
        {
            var result = await ReadAsync(root, expected, false, ct);
            return result.IsSuccess
                ? Result<System.Collections.Immutable.ImmutableArray<WorkItem>>.Ok([.. result.Value.Snapshot.Items.Where(item => keys.Contains(item.Key))])
                : Result<System.Collections.Immutable.ImmutableArray<WorkItem>>.Fail(result.Error);
        }
        internal System.Collections.Concurrent.ConcurrentBag<string> Reads { get; } = [];
        private string? blocked, later;
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LaterRead { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Block(string first, string second)
        {
            blocked = first; later = second;
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LaterRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
        internal void Replace(string root, NativeSourceObservation observation)
            => observations[root] = observation with { Source = observations[root].Source };
        internal void ReplaceStore(string root)
            => observations[root] = observations[root] with
            { Source = observations[root].Source with { StoreInstance = Guid.NewGuid() } };
        internal void Exclude(string root, Selector rule)
            => observations[root] = observations[root] with { WorkspaceExclusions = [rule] };
        public async Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Reads.Add(root);
            if (root == blocked) await Release.Task.WaitAsync(ct);
            if (root == later) LaterRead.TrySetResult();
            if (Denied.Contains(root)) return Result<NativeSourceObservation>.Fail(new Error(ErrorCode.Unavailable, "Synthetic native denial") { Evidence = ["http:403"] });
            if (!observations.TryGetValue(root, out var value)) return Result<NativeSourceObservation>.Fail(ErrorCode.NotFound, "Unknown synthetic root");
            if (expected is not null && value.Source != expected) return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic source changed");
            return Result<NativeSourceObservation>.Ok(value with
                { CacheReadAt = DateTimeOffset.UtcNow, Schema = observeSchema ? value.Schema : null, SchemaObservedAt = observeSchema ? DateTimeOffset.UtcNow : null });
        }
    }
}
