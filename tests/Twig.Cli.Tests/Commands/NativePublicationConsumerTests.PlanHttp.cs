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
    // Canvas PlanTests.cs, blob ee4b11096ba08cdbe6709f747e5ed387f6d750d6.
    [Theory]
    [InlineData("full")]
    [InlineData("delayed")]
    [InlineData("navigation")]
    [InlineData("preparation")]
    [InlineData("selection")]
    public async Task NativeAdoCompositionHttpKeepsExactAuthority(string scenario)
    {
        var caseClock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        using var callbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ct = deadline.Token;
        var fixture = await CreateHumanHostAsync().WaitAsync(ct);
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var gateway = new NativeHttpGateway(this);
        var changes = new CatalogChanges();
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "plan-http-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "plan-http-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, Guid.NewGuid());
        var owned = sessions.Content(human.ViewId, human.ContentCapability, true);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        await app.StartAsync(ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? callback = null;
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
                var requestClock = Stopwatch.StartNew();
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (requestClock.Elapsed > TimeSpan.FromSeconds(10))
                    _agentHttpOutput?.WriteLine($"TIMING WARNING: ADO Plan {scenario} {method} {path} exceeded 10s request target ({requestClock.ElapsedMilliseconds}ms)");
                text.ShouldNotBeNullOrWhiteSpace(path);
                return (response.StatusCode, JsonNode.Parse(text)!);
            }
            async Task<JsonNode> Ok(string path, HttpMethod method, JsonNode? body = null)
            {
                var response = await Send(path, method, body);
                (response.Status is HttpStatusCode.OK or HttpStatusCode.Accepted)
                    .ShouldBeTrue(path + ": " + response.Body.ToJsonString());
                return response.Body;
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
            JsonObject BenchBody() => new() { ["benchId"] = fixture.Bench.ToString("D") };
            async Task<JsonNode> Mode(bool enabled)
            {
                var status = await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get);
                var body = BenchBody();
                body["storeId"] = status["storeId"]!.DeepClone();
                body["benchRevision"] = status["benchRevision"]!.DeepClone();
                body["planRevision"] = status["revision"]!.DeepClone();
                body["enabled"] = enabled;
                return await Ok("/plan/mode", HttpMethod.Put, body);
            }
            Task<JsonNode> View() => Ok($"/benches/{fixture.Bench:D}", HttpMethod.Get);
            async Task<JsonNode> Open(JsonNode source, JsonNode item, bool plan)
            {
                var body = BenchBody();
                body["sourceId"] = source["sourceId"]!.DeepClone();
                body["itemId"] = item["id"]!.DeepClone();
                body["planMode"] = plan;
                return await Ok("/editor", HttpMethod.Post, body);
            }
            async Task<JsonNode> Wait(JsonNode operation, bool expectSuccess = true)
            {
                var id = operation["operationId"]!.GetValue<string>();
                while (operation["status"]!.GetValue<string>() is "Reviewing" or "Applying" or "Delivering")
                {
                    await Task.Delay(500, ct);
                    operation = await Ok("/plan/operations/" + id, HttpMethod.Get);
                    operation["operationId"]!.GetValue<string>().ShouldBe(id);
                }
                if (expectSuccess) operation["error"].ShouldBeNull(operation.ToJsonString());
                return operation;
            }
            async Task<int> Captures()
            {
                var captures = await store.ReadEditorCapturesAsync(ct);
                captures.IsSuccess.ShouldBeTrue(captures.IsSuccess ? "" : captures.Error.Message);
                return captures.Value.Length;
            }
            async Task Report(long reviewElapsedMs = 0)
            {
                _agentHttpOutput?.WriteLine($"PLAN HTTP {scenario}: providerHttp={_transport.TotalRequests}, " +
                    $"captures={await Captures()}, patches={HumanPatchAttempts}, creates={_transport.PostCount}, " +
                    $"committed={_transport.CommittedWrites}, prepared={gateway.PreparedEditors}, " +
                    $"existing={gateway.ExistingEditors}, reviews={gateway.PlanReviews}, " +
                    $"openReviews={gateway.OpenReviews}, reviewElapsedMs={reviewElapsedMs}");
            }
            void NoWrites()
            {
                HumanPatchAttempts.ShouldBe(0);
                _transport.PostCount.ShouldBe(0);
                _transport.CommittedWrites.ShouldBe(0);
            }

            await Ok("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            await Ok("/selection", HttpMethod.Put, new JsonObject { ["expectedBenchId"] = null, ["benchId"] = fixture.Bench });
            var view = await View();
            var source = view["sources"]!.AsArray().Single()!;
            source["errorCode"].ShouldBeNull();
            var items = source["items"]!.AsArray();
            items.Count.ShouldBe(2);
            var first = items.Single(item => item!["locator"]!.GetValue<string>() == "42")!;
            var second = items.Single(item => item!["locator"]!.GetValue<string>() == "43")!;
            var capturesBefore = await Captures();
            var requestsBefore = _transport.TotalRequests;
            foreach (var route in new[] { "/plan/review", "/plan/seed", "/editor/stage", "/plan/apply" })
                (await Send(route, HttpMethod.Post, new JsonObject(), canvas.PresentationCapability))
                    .Status.ShouldBe(HttpStatusCode.Forbidden, "SDK cannot invoke " + route);
            _transport.TotalRequests.ShouldBe(requestsBefore);
            (await Captures()).ShouldBe(capturesBefore);
            NoWrites();

            if (scenario == "delayed")
            {
                var before = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                before.Enabled.ShouldBeFalse();
                before.Sources.ShouldBeEmpty();
                (await store.ChangePlanModeAsync(before.UserInstance, fixture.Bench,
                    before.BenchRevision, before.Revision, true, ct)).IsSuccess.ShouldBeTrue();
                using (var editor = (await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct)).Value)
                {
                    editor.PlanMode.ShouldBeTrue();
                    editor.StoreInstance.ShouldNotBe(fixture.Stamp.StoreInstance);
                    var form = (await editor.SelectAsync(ItemKey.Remote(fixture.Stamp.Connection.Provider,
                        fixture.Stamp.Connection.ResourceScope, "42"), ct)).Value;
                    (await editor.StageAsync(form.Ticket,
                        FieldValues.Empty.Set("System.Title", FieldValue.String("Planned delay observation")), ct)).IsSuccess.ShouldBeTrue();
                    (await editor.ReadPlanDraftsAsync(ct)).Value.ShouldHaveSingleItem();
                }
                NoWrites();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var injected = 0;
                HumanBeforeProjectMetadata = () =>
                {
                    Interlocked.Increment(ref injected).ShouldBe(1);
                    HumanBeforeProjectMetadata = null;
                    entered.TrySetResult();
                    callback = Task.Delay(36000, callbackLifetime.Token);
                    return callback;
                };
                var clock = Stopwatch.StartNew();
                var operation = await Ok("/plan/review", HttpMethod.Post, BenchBody());
                clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                var id = operation["operationId"]!.GetValue<string>();
                var observed = await Ok("/plan/operations/" + id, HttpMethod.Get);
                observed["status"]!.GetValue<string>().ShouldBe("Reviewing");
                observed["review"].ShouldBeNull();
                var completed = await Wait(operation);
                clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(36000));
                completed["status"]!.GetValue<string>().ShouldBe("Reviewed");
                completed["review"]!["effects"]!.AsArray().Count.ShouldBe(1);
                injected.ShouldBe(1);
                gateway.PlanReviews.ShouldBe(1);
                (await Captures()).ShouldBeGreaterThan(capturesBefore);
                NoWrites();
                await Report(clock.ElapsedMilliseconds);
                return;
            }

            var normal = await Open(source, first, false);
            normal["binding"]!["planMode"]!.GetValue<bool>().ShouldBeFalse();
            if (scenario == "preparation")
            {
                var before = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                var injected = 0;
                HumanBeforeProjectMetadata = () =>
                {
                    Interlocked.Increment(ref injected);
                    return Task.FromException(new IOException("Synthetic Plan-enable metadata failure"));
                };
                var body = BenchBody();
                body["storeId"] = before.UserInstance.ToString("D");
                body["benchRevision"] = before.BenchRevision;
                body["planRevision"] = before.Revision;
                body["enabled"] = true;
                var failed = await Send("/plan/mode", HttpMethod.Put, body);
                failed.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
                failed.Body["message"]!.GetValue<string>().ShouldContain("Plan mode was enabled");
                var after = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                after.Enabled.ShouldBeTrue();
                after.Revision.ShouldBeGreaterThan(before.Revision);
                (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["enabled"]!.GetValue<bool>().ShouldBeTrue();
                injected.ShouldBe(1);
                NoWrites();
                await Report();
                return;
            }

            var preparedBefore = gateway.PreparedEditors;
            await Mode(true);
            gateway.PreparedEditors.ShouldBe(preparedBefore + 1, "Explicit enable prepares exactly once.");
            var enabled = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
            enabled.Enabled.ShouldBeTrue();
            var scope = enabled.Sources.ShouldHaveSingleItem();
            scope.Source.ShouldBe(fixture.Stamp);
            scope.OriginRoot.ShouldBe(fixture.Root);
            NoWrites();
            (await Send("/editor/save", HttpMethod.Post, Change(normal,
                Field("System.Title", "Text", "Must not leak")))).Status.ShouldBe(HttpStatusCode.Conflict);
            var prepared = gateway.PreparedEditors;
            var existing = gateway.ExistingEditors;
            JsonNode last = normal;
            foreach (var item in new[] { first, second })
            {
                var before = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                last = await Open(source, item, true);
                var after = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                (after.PlanId, after.Revision, after.BenchRevision).ShouldBe((before.PlanId, before.Revision, before.BenchRevision));
                after.Sources.ShouldBe(before.Sources);
                gateway.PreparedEditors.ShouldBe(prepared);
                last["binding"]!["planMode"]!.GetValue<bool>().ShouldBeTrue();
                last["binding"]!["storeInstance"]!.GetValue<string>().ShouldNotBe(fixture.Stamp.StoreInstance.ToString("D"));
                if (scenario != "selection")
                    last = await Ok("/editor/stage", HttpMethod.Post, Change(last,
                        Field("System.Title", "Text", "Planned " + item["locator"]!.GetValue<string>())));
            }
            gateway.ExistingEditors.ShouldBe(existing + 2);
            NoWrites();
            if (scenario == "selection")
            {
                (await Captures()).ShouldBe(capturesBefore, "Selection alone creates no staged capture.");
                await Report();
                return;
            }
            (await Captures()).ShouldBeGreaterThan(capturesBefore);
            view = await View();
            var overlays = view["sources"]![0]!["items"]!.AsArray();
            overlays.Count.ShouldBe(2);
            foreach (var item in overlays)
            {
                item!["title"]!.GetValue<string>().ShouldBe("Planned " + item["locator"]!.GetValue<string>());
                item["hasDraft"]!.GetValue<bool>().ShouldBeTrue();
            }
            if (scenario == "navigation")
            {
                var apply = BenchBody();
                apply["stagedVersion"] = (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["stagedVersion"]!.DeepClone();
                NoWrites();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var injected = 0;
                HumanBeforeProjectMetadata = () =>
                {
                    Interlocked.Increment(ref injected).ShouldBe(1);
                    HumanBeforeProjectMetadata = null;
                    entered.TrySetResult();
                    callback = release.Task.WaitAsync(callbackLifetime.Token);
                    return callback;
                };
                var operation = await Ok("/plan/apply", HttpMethod.Post, apply);
                var id = operation["operationId"]!.GetValue<string>();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                (await Ok("/plan/operations/" + id, HttpMethod.Get))["status"]!.GetValue<string>().ShouldBe("Applying");
                var blocked = await Send("/plan/apply", HttpMethod.Post, apply);
                blocked.Status.ShouldBe(HttpStatusCode.Conflict);
                blocked.Body["code"]!.GetValue<string>().ShouldBe("PlanBusy");
                await Ok("/editor/close", HttpMethod.Post, new JsonObject { ["ticket"] = last["ticket"]!.DeepClone() });
                blocked = await Send("/plan/apply", HttpMethod.Post, apply);
                blocked.Status.ShouldBe(HttpStatusCode.Conflict);
                blocked.Body["code"]!.GetValue<string>().ShouldBe("PlanBusy");
                release.TrySetResult();
                var completed = await Wait(operation, expectSuccess: false);
                completed["error"]!["code"]!.GetValue<string>().ShouldBe("PlanNavigationChanged");
                completed["review"].ShouldBeNull();
                var digest = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value.LastReviewDigest;
                digest.ShouldBeNull("Navigation changed before an attempt was admitted.");
                gateway.PlanReviews.ShouldBe(1);
                gateway.OpenReviews.ShouldBe(0);
                injected.ShouldBe(1);
                NoWrites();
                await Report();
                return;
            }
            scenario.ShouldBe("full");
            var opened = await panel.OpenAsync(fixture.Root, ct);
            opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
            var anchor = opened.Value;
            var capture = await panel.ReadViewAsync(fixture.Bench, anchor, [anchor], ct);
            capture.IsSuccess.ShouldBeTrue(capture.IsSuccess ? "" : capture.Error.Message);
            var captured = capture.Value;
            captured.Plan.Enabled.ShouldBeTrue();
            captured.View.Sources.Single().Items.ShouldAllBe(item => item.HasDraft);
            await Mode(false);
            captured.Plan.Enabled.ShouldBeTrue("A later mode transition cannot relabel the actual immutable native composition.");
            captured.View.Sources.Single().Items.ShouldAllBe(item => item.HasDraft);
            normal = await Open(source, first, false);
            var saved = await Ok("/editor/save", HttpMethod.Post, Change(normal,
                Field("System.Description", "Html", "<p>Normal only description</p>")));
            saved["verified"]!.GetValue<bool>().ShouldBeTrue();
            HumanPatchAttempts.ShouldBe(1);
            _transport.PostCount.ShouldBe(0);
            using var services = EditorServices(fixture.Root);
            using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(ct)).Value;
            var original = (await origin.Workspace.SnapshotAsync(ct)).Value;
            var original42 = original.Items.Single(item => item.Key.NativeId == "42");
            original42.Fields["System.Description"].Text.ShouldBe("<p>Normal only description</p>");
            original42.Fields["System.Title"].Text.ShouldBe(first["title"]!.GetValue<string>());
            original.RetainedItems.ShouldBeEmpty();
            original.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Verified);
            original.Plans.Single().Operations.Single().Fields.Keys.ShouldBe(new[] { "System.Description" });
            await Mode(true);
            view = await View();
            view["sources"]![0]!["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>())
                .Order().ShouldBe(new[] { "Planned 42", "Planned 43" });
            var priorStagedVersion = (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["stagedVersion"]!.DeepClone();
            var seedBody = BenchBody();
            seedBody["sourceId"] = source["sourceId"]!.DeepClone();
            seedBody["type"] = normal["type"]!.DeepClone();
            var seed = await Ok("/plan/seed", HttpMethod.Post, seedBody);
            var stateField = seed["stateField"]!.GetValue<string>();
            var initial = normal["fields"]!.AsArray().Single(field =>
                field!["reference"]!.GetValue<string>() == stateField)!["value"]!.DeepClone();
            await Ok("/editor/stage", HttpMethod.Post, Change(seed,
                Field("System.Title", "Text", "Planned local seed"),
                new JsonObject { ["reference"] = stateField, ["value"] = initial }));
            view = await View();
            var members = view["sources"]![0]!["items"]!.AsArray();
            members.Count(item => item!["benchMember"]!.GetValue<bool>()).ShouldBe(3);
            members.Single(item => item!["isLocal"]!.GetValue<bool>())!["webUrl"].ShouldBeNull();
            HumanPatchAttempts.ShouldBe(1);
            _transport.PostCount.ShouldBe(0);
            var applyBody = BenchBody();
            applyBody["stagedVersion"] = priorStagedVersion;
            (await Wait(await Ok("/plan/apply", HttpMethod.Post, applyBody), expectSuccess: false))["error"]!["code"]!
                .GetValue<string>().ShouldBe("Conflict");
            HumanPatchAttempts.ShouldBe(1);
            _transport.PostCount.ShouldBe(0);
            applyBody["stagedVersion"] = (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["stagedVersion"]!.DeepClone();
            var completedApply = await Wait(await Ok("/plan/apply", HttpMethod.Post, applyBody));
            completedApply["outcome"]!["status"]!.GetValue<string>().ShouldBe("Complete");
            completedApply["outcome"]!["verifiedOperations"]!.GetValue<int>().ShouldBe(3);
            completedApply["outcome"]!["deliveredScopes"]!.GetValue<int>().ShouldBe(1);
            HumanPatchAttempts.ShouldBe(3);
            _transport.PostCount.ShouldBe(1);
            await Report();
            _transport.CommittedWrites.ShouldBe(4, "One normal Save, two Plan edits and one synthetic creation.");
            (await Wait(await Ok("/plan/apply", HttpMethod.Post, applyBody), expectSuccess: false))["outcome"].ShouldBeNull();
            var planScope = (await store.ReadPlanAsync(fixture.Bench, ct)).Value.Scopes.Single();
            var planStore = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(planScope.DatabasePath, ct, planScope.StoreInstance)).Value;
            var planSnapshot = (await planStore.ReadAsync(fixture.Stamp.Connection, ct)).Value;
            var mapping = planSnapshot.Mappings.ShouldHaveSingleItem();
            var owner = (await store.ReadPlanSeedOwnerAsync(mapping.Local.LocalId, ct)).Value;
            owner.ScopeId.ShouldBe(planScope.Id);
            planSnapshot.Journal.Count().ShouldBe(3);
            planSnapshot.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
            foreach (var row in planSnapshot.Journal)
            {
                row.Receipts.Length.ShouldBe(2, "Keep both the actual dispatch receipt and its separate readback verification.");
                var receipt = row.Receipts.Single(value => value.Status == OpStatus.Applied);
                var verifiedReceipt = row.Receipts.Single(value => value.Status == OpStatus.Verified);
                receipt.Digest.ShouldBe(row.Digest);
                receipt.Attempt.ShouldBe(row.Attempt);
                receipt.Revision.ShouldNotBeNull();
                receipt.Error.ShouldBeNull();
                (verifiedReceipt.Digest, verifiedReceipt.Operation, verifiedReceipt.Attempt, verifiedReceipt.Revision,
                    verifiedReceipt.PublishedKey).ShouldBe((receipt.Digest, receipt.Operation, receipt.Attempt,
                        receipt.Revision, receipt.PublishedKey));
                verifiedReceipt.Error.ShouldBeNull();
            }
            planSnapshot.Journal.SelectMany(row => row.Receipts)
                .Single(receipt => receipt.Status == OpStatus.Applied && receipt.PublishedKey is not null)
                .PublishedKey.ShouldBe(mapping.Remote);
            var selectors = (await store.ReadAsync(ct)).Value.Benches.Single().Selectors;
            selectors.Length.ShouldBe(3);
            selectors.ShouldContain(selector => selector.Item == mapping.Remote);
            foreach (var id in new[] { "42", "43" })
                selectors.ShouldContain(selector => selector.Item != null && selector.Item.NativeId == id);
            original = (await origin.Workspace.SnapshotAsync(ct)).Value;
            original.RetainedItems.ShouldBeEmpty();
            original.Items.Single(item => item.Key == mapping.Remote).Fields["System.Title"].Text.ShouldBe("Planned local seed");
            original.Items.Single(item => item.Key.NativeId == "42").Fields["System.Title"].Text.ShouldBe("Planned 42");
            original.Items.Single(item => item.Key.NativeId == "43").Fields["System.Title"].Text.ShouldBe("Planned 43");
            (await store.ReadEditorCapturesAsync(ct)).Value.ShouldAllBe(value => value.Settled);
            view = await View();
            members = view["sources"]![0]!["items"]!.AsArray();
            members.Count(item => item!["benchMember"]!.GetValue<bool>()).ShouldBe(3);
            members.ShouldAllBe(item => !item!["isLocal"]!.GetValue<bool>());
            HumanPatchAttempts.ShouldBe(3);
            _transport.PostCount.ShouldBe(1);
        }
        finally
        {
            if (caseClock.Elapsed > TimeSpan.FromSeconds(55))
                _agentHttpOutput?.WriteLine($"TIMING WARNING: ADO Plan {scenario} case exceeded 55s target ({caseClock.ElapsedMilliseconds}ms)");
            HumanBeforeProjectMetadata = null;
            release.TrySetResult();
            await callbackLifetime.CancelAsync();
            if (callback is not null)
            {
                try { await callback; }
                catch (OperationCanceledException) when (callbackLifetime.IsCancellationRequested) { }
            }
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }
}
