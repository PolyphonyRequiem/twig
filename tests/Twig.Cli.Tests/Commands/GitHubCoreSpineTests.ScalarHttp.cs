using System.Collections.Immutable;
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

public sealed partial class GitHubCoreSpineTests
{
    // GitHubEditorTests.cs, Canvas blob a3ca89da602a9e6f13e0362a952482a79c5bb13a.
    [Theory]
    [InlineData("save")]
    [InlineData("guards")]
    [InlineData("plan")]
    [InlineData("apply")]
    public async Task NativeGitHubScalarHttpUsesQualifiedNativeAuthority(string scenario)
    {
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var ct = deadline.Token;
        using var native = new HumanEditorFixture();
        void Phase(string phase) => _linkHttpOutput?.WriteLine(
            $"GitHub scalar {scenario} {clock.Elapsed.TotalMilliseconds:F0}ms: {phase}; providerRequests={native.Native.Http.Requests}, writes={native.Writes}");
        Phase("initializing fixture");
        var fixture = await native.InitializeAsync().WaitAsync(ct);
        Phase("fixture ready");
        fixture.Stamp.Connection.Provider.Value.ShouldBe("github");
        native.Writes.ShouldBe(0);
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var gateway = new GitHubScalarHttpGateway(native);
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
                response.Status.ShouldBe(path is "/plan/review" or "/plan/apply"
                    ? HttpStatusCode.Accepted : HttpStatusCode.OK, path + ": " + response.Body.ToJsonString());
                return response.Body;
            }
            async Task<JsonNode> Mode(bool enabled)
            {
                var status = await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get);
                return await Ok("/plan/mode", HttpMethod.Put, new JsonObject
                {
                    ["benchId"] = fixture.Bench.ToString("D"), ["storeId"] = status["storeId"]!.DeepClone(),
                    ["benchRevision"] = status["benchRevision"]!.DeepClone(),
                    ["planRevision"] = status["revision"]!.DeepClone(), ["enabled"] = enabled,
                });
            }
            async Task<JsonNode> Wait(JsonNode operation, bool expectSuccess = true)
            {
                var id = operation["operationId"]!.GetValue<string>();
                while (operation["status"]!.GetValue<string>() is "Reviewing" or "Applying" or "Delivering")
                {
                    await Task.Delay(100, ct);
                    operation = await Ok("/plan/operations/" + id, HttpMethod.Get);
                    operation["operationId"]!.GetValue<string>().ShouldBe(id);
                }
                if (expectSuccess) operation["error"].ShouldBeNull(operation.ToJsonString());
                return operation;
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
            source["provider"]!.GetValue<string>().ShouldBe("github");
            source["errorCode"].ShouldBeNull();
            var item = source["items"]!.AsArray().Single()!;
            item["locator"]!.GetValue<string>().ShouldBe("42");
            item["titleField"]!.GetValue<string>().ShouldBe("github.title");
            var open = new JsonObject
            {
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["itemId"] = item["id"]!.DeepClone(),
                ["benchId"] = fixture.Bench.ToString("D"), ["planMode"] = false,
            };
            var http = native.Native.Http.Requests;
            foreach (var route in new[] { "/editor", "/editor/evaluate", "/editor/save", "/editor/stage", "/plan/review", "/plan/apply" })
                (await Send(route, HttpMethod.Post, new JsonObject(), canvas.PresentationCapability))
                    .Status.ShouldBe(HttpStatusCode.Forbidden, "SDK presentation capability must not invoke " + route);
            client.DefaultRequestHeaders.Remove("X-Twig-Bench");
            (await Send("/editor", HttpMethod.Post, open)).Status.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            native.Native.Http.Requests.ShouldBe(http, "Capability/CSRF guards run before provider transport.");
            native.Writes.ShouldBe(0);
            var form = await Ok("/editor", HttpMethod.Post, open);
            form["binding"]!["planMode"]!.GetValue<bool>().ShouldBeFalse();
            form["stateField"]!.GetValue<string>().ShouldBe("github.state");
            var notice = form["notice"]!.GetValue<string>();
            notice.ShouldContain("observed-non-atomic");
            notice.ShouldContain("automation rules are not discoverable");
            var fields = form["fields"]!.AsArray();
            fields.Single(field => field!["reference"]!.GetValue<string>() == "github.body")!
                ["kind"]!.GetValue<string>().ShouldBe("Markdown");
            fields.Single(field => field!["reference"]!.GetValue<string>() == "github.assigneeIds")!
                ["readOnly"]!.GetValue<bool>().ShouldBeTrue();

            if (scenario == "save")
            {
                const string markdown = "**unchanged Markdown**\n\n<script>not HTML</script>";
                var saved = await Ok("/editor/save", HttpMethod.Post, Change(form,
                    Field("github.title", "Text", "HTTP native title"),
                    Field("github.body", "Markdown", markdown),
                    Field("github.state", "Text", "CLOSED")));
                saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                native.Writes.ShouldBe(1, "One updateIssue carries all three scalar fields.");
                native.Native.Http.Creates.ShouldBe(0);
                native.Native.Http.Comment.ShouldBeNull();
                var remote = native.Native.Http.Issue;
                remote["title"]!.GetValue<string>().ShouldBe("HTTP native title");
                remote["body"]!.GetValue<string>().ShouldBe(markdown, "Literal script text must remain Markdown, not HTML coercion.");
                remote["state"]!.GetValue<string>().ShouldBe("CLOSED");
                var snapshot = await native.SnapshotAsync().WaitAsync(ct);
                snapshot.RetainedItems.ShouldBeEmpty();
                snapshot.Journal.Single().Status.ShouldBe(OpStatus.Verified);
                var operation = snapshot.Plans.Single().Operations.Single();
                operation.Concurrency.ShouldBe(ConcurrencyMode.ObservedNonAtomic);
                operation.Fields.Count.ShouldBe(3);
                var actual = snapshot.Items.Single().Fields;
                actual["github.title"].Text.ShouldBe("HTTP native title");
                actual["github.body"].ShouldBe(FieldValue.RichText(markdown, FieldKind.Markdown));
                actual["github.state"].Text.ShouldBe("CLOSED");
                form = await Ok("/editor", HttpMethod.Post, open);
                saved = await Ok("/editor/save", HttpMethod.Post, Change(form, Field("github.body", "Markdown", "")));
                saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                native.Writes.ShouldBe(2, "An explicit empty Markdown clear makes exactly one second verified write.");
                remote["body"]!.GetValue<string>().ShouldBe("");
                snapshot = await native.SnapshotAsync().WaitAsync(ct);
                snapshot.Items.Single().Fields["github.body"].ShouldBe(FieldValue.RichText("", FieldKind.Markdown));
                snapshot.RetainedItems.ShouldBeEmpty();
                snapshot.Journal.Count().ShouldBe(2);
                snapshot.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
            }
            else if (scenario is "plan" or "apply")
            {
                var capturesBefore = (await store.ReadEditorCapturesAsync(ct)).Value.Length;
                await Mode(true);
                (await Send("/editor/save", HttpMethod.Post, Change(form, Field("github.title", "Text", "stale mode"))))
                    .Status.ShouldBe(HttpStatusCode.Conflict);
                open["planMode"] = true;
                form = await Ok("/editor", HttpMethod.Post, open);
                form["binding"]!["storeInstance"]!.GetValue<string>().ShouldNotBe(fixture.Stamp.StoreInstance.ToString("D"));
                var staged = await Ok("/editor/stage", HttpMethod.Post, Change(form,
                    Field("github.title", "Text", "Planned HTTP title"),
                    Field("github.body", "Markdown", "**planned Markdown**"), Field("github.state", "Text", "CLOSED")));
                staged["binding"]!["planMode"]!.GetValue<bool>().ShouldBeTrue();
                var planId = staged["binding"]!["planId"]!.GetValue<string>();
                var scopeId = staged["binding"]!["scopeId"]!.GetValue<string>();
                Guid.Parse(planId).ShouldNotBe(Guid.Empty);
                Guid.Parse(scopeId).ShouldNotBe(Guid.Empty);
                native.Writes.ShouldBe(0);
                (await store.ReadEditorCapturesAsync(ct)).Value.Length.ShouldBeGreaterThan(capturesBefore);
                (await Send("/editor/save", HttpMethod.Post, Change(staged, Field("github.title", "Text", "must not save"))))
                    .Status.ShouldBe(HttpStatusCode.Conflict);
                (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["lastReviewDigest"].ShouldBeNull();
                if (scenario == "apply")
                {
                    var apply = new JsonObject
                    {
                        ["benchId"] = fixture.Bench.ToString("D"),
                        ["stagedVersion"] = (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["stagedVersion"]!.DeepClone(),
                    };
                    native.Writes.ShouldBe(0);
                    var operation = await Wait(await Ok("/plan/apply", HttpMethod.Post, apply));
                    operation["outcome"]!["status"]!.GetValue<string>().ShouldBe("Complete");
                    operation["outcome"]!["verifiedOperations"]!.GetValue<int>().ShouldBe(1);
                    operation["outcome"]!["deliveredScopes"]!.GetValue<int>().ShouldBe(1);
                    native.Writes.ShouldBe(1);
                    var repeated = await Wait(await Ok("/plan/apply", HttpMethod.Post, apply), expectSuccess: false);
                    repeated["error"]!["code"]!.GetValue<string>().ShouldBe("Conflict");
                    gateway.PlanReviews.ShouldBe(1);
                    var delivered = await native.SnapshotAsync().WaitAsync(ct);
                    delivered.RetainedItems.ShouldBeEmpty();
                    var fieldsAfter = delivered.Items.Single().Fields;
                    fieldsAfter["github.title"].Text.ShouldBe("Planned HTTP title");
                    fieldsAfter["github.body"].ShouldBe(FieldValue.RichText("**planned Markdown**", FieldKind.Markdown));
                    fieldsAfter["github.state"].Text.ShouldBe("CLOSED");
                    native.Native.Http.Issue["title"]!.GetValue<string>().ShouldBe("Planned HTTP title");
                    native.Native.Http.Issue["body"]!.GetValue<string>().ShouldBe("**planned Markdown**");
                    native.Native.Http.Issue["state"]!.GetValue<string>().ShouldBe("CLOSED");
                    (await store.ReadEditorCapturesAsync(ct)).Value.ShouldAllBe(value => value.Settled);
                }
                else
                {
                    var review = await Wait(await Ok("/plan/review", HttpMethod.Post,
                        new JsonObject { ["benchId"] = fixture.Bench.ToString("D") }));
                    review["status"]!.GetValue<string>().ShouldBe("Reviewed");
                    var exact = review["review"]!;
                    exact["effects"]!.AsArray().Count.ShouldBe(1);
                    exact["notice"]!.GetValue<string>().ShouldContain("observed-non-atomic");
                    (await Ok($"/benches/{fixture.Bench:D}/plan", HttpMethod.Get))["lastReviewDigest"].ShouldBeNull();
                    native.Writes.ShouldBe(0);
                    gateway.PlanReviews.ShouldBe(1);
                    await Mode(false);
                    open["planMode"] = false;
                    form = await Ok("/editor", HttpMethod.Post, open);
                    var saved = await Ok("/editor/save", HttpMethod.Post,
                        Change(form, Field("github.body", "Markdown", "Normal-only body")));
                    saved["verified"]!.GetValue<bool>().ShouldBeTrue();
                    saved["draftRetained"]!.GetValue<bool>().ShouldBeFalse();
                    var original = await native.SnapshotAsync().WaitAsync(ct);
                    original.Items.Single().Fields["github.title"].Text.ShouldBe("original");
                    original.Items.Single().Fields["github.body"].ShouldBe(FieldValue.RichText("Normal-only body", FieldKind.Markdown));
                    original.Items.Single().Fields["github.state"].Text.ShouldBe("OPEN");
                    original.RetainedItems.ShouldBeEmpty();
                    original.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Verified);
                    original.Plans.Single().Operations.Single().Fields.Keys.ShouldBe(new[] { "github.body" });
                    native.Writes.ShouldBe(1);
                    await Mode(true);
                    open["planMode"] = true;
                    form = await Ok("/editor", HttpMethod.Post, open);
                    form["binding"]!["planId"]!.GetValue<string>().ShouldBe(planId);
                    form["binding"]!["scopeId"]!.GetValue<string>().ShouldBe(scopeId);
                    form["fields"]!.AsArray().Single(field => field!["reference"]!.GetValue<string>() == "github.title")!
                        ["value"]!["value"]!.GetValue<string>().ShouldBe("Planned HTTP title");
                    native.Native.Http.Issue["title"]!.GetValue<string>().ShouldBe("original");
                    native.Native.Http.Issue["body"]!.GetValue<string>().ShouldBe("Normal-only body");
                    native.Native.Http.Issue["state"]!.GetValue<string>().ShouldBe("OPEN");
                }
                native.Writes.ShouldBe(1, "Delivery, reopen and refused repeated Apply cannot replay any effect.");
                native.Native.Http.Creates.ShouldBe(0);
                native.Native.Http.Comment.ShouldBeNull();
                native.Native.Http.Requests.ShouldBeGreaterThan(http);
            }
            else
            {
                scenario.ShouldBe("guards");
                (await Send("/editor/save", HttpMethod.Post, Change(form, Field("github.body", "Html", "<p>wrong</p>"))))
                    .Status.ShouldBe(HttpStatusCode.BadRequest, "GitHub body input cannot be coerced from HTML.");
                (await Send("/editor/save", HttpMethod.Post, Change(form, Field("System.Title", "Text", "alias"))))
                    .Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "ADO aliases are not GitHub-native fields.");
                native.Writes.ShouldBe(0);
                await Ok("/editor/close", HttpMethod.Post, new JsonObject { ["ticket"] = form["ticket"]!.DeepClone() });
                (await Send("/editor/save", HttpMethod.Post, Change(form, Field("github.title", "Text", "stale"))))
                    .Status.ShouldBe(HttpStatusCode.Conflict);
                native.Writes.ShouldBe(0);
                form = await Ok("/editor", HttpMethod.Post, open);
                native.Defect = "actor";
                var denied = await Send("/editor/save", HttpMethod.Post, Change(form, Field("github.title", "Text", "wrong actor")));
                (denied.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict).ShouldBeTrue(denied.Body.ToJsonString());
                native.Writes.ShouldBe(0, "Every guard path stops before updateIssue.");
                native.Native.Http.Issue["title"]!.GetValue<string>().ShouldBe("original");
                native.Native.Http.Issue["body"]!.GetValue<string>().ShouldBe("");
                native.Native.Http.Issue["state"]!.GetValue<string>().ShouldBe("OPEN");
            }
        }
        finally
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(55))
                Phase("TIMING WARNING: case exceeded 55s target");
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class GitHubScalarHttpGateway(HumanEditorFixture fixture) : INativeSourceGateway
    {
        internal int PlanReviews { get; private set; }

        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected,
            bool observeSchema, CancellationToken ct) => fixture.ReadAsync(root, expected, observeSchema, ct);

        public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected,
            CancellationToken ct) => fixture.ReadOnlineAsync(root, expected, ct);


        public Task<Result<NativeItemEditor>> OpenEditorAsync(string root, Guid bench,
            NativeSourceStamp expected, CancellationToken ct) => fixture.OpenAsync(root, bench, expected, ct);

        public Task<Result<NativeItemEditor>> OpenExistingEditorAsync(string root, Guid bench,
            NativeSourceStamp expected, CancellationToken ct)
            => NativeItemEditor.OpenAsync(root, bench, expected, fixture.Configure, ct, prepareScope: false);

        public Task<Result<NativePlanReview>> ReviewPlanAsync(string root, Guid bench,
            ImmutableArray<NativePlanSource> sources, CancellationToken ct)
        {
            PlanReviews++;
            return fixture.ReviewAsync(root, bench, sources, ct);
        }

        public Task<Result<NativePlanReview>> OpenPlanReviewAsync(string root, string digest, CancellationToken ct)
            => fixture.OpenReviewAsync(root, digest, ct);
    }
}
