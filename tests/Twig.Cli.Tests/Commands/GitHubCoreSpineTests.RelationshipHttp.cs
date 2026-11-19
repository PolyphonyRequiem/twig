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
using Xunit.Abstractions;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    private readonly ITestOutputHelper? _linkHttpOutput;

    public GitHubCoreSpineTests(ITestOutputHelper? output = null) => _linkHttpOutput = output;

    [Theory]
    [InlineData("counterfactual")]
    [InlineData("guards")]
    [InlineData("lifecycle")]
    [InlineData("protected")]
    [InlineData("delivery")]
    [InlineData("binding-actor")]
    [InlineData("binding-repository")]
    [InlineData("binding-schema")]
    [InlineData("binding-endpoint")]
    [InlineData("binding-mode")]
    [InlineData("binding-store")]
    [InlineData("binding-bench")]
    [InlineData("binding-denied")]
    [InlineData("binding-generation")]
    public async Task NativeGitHubLinkHttpKeepsExactAuthority(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        using var native = new LocalHttpFixture();
        await native.InitializeAsync().WaitAsync(ct);
        var fixture = native.Human.Host;
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new GitHubScalarHttpGateway(native.Human);
        var updates = new CatalogChanges();
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "local-http-evidence"), gateway, updates));
        var sessions = new ViewSessions(Path.Combine(directory, "local-http-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var profile = Guid.NewGuid();
        var human = sessions.ConnectOwner(instance, canvas.ViewId, profile);
        var owned = sessions.Content(human.ViewId, human.ContentCapability, true);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, updates);
        try
        {
            await app.StartAsync(ct);
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                Timeout = TimeSpan.FromSeconds(55),
            };
            async Task<(int Status, JsonNode Body)> Request(string path, JsonNode? body = null, HttpMethod? method = null,
                string? capability = null, bool csrf = true)
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                using var request = new HttpRequestMessage(method ?? HttpMethod.Post, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability ?? human.ContentCapability);
                if (csrf) request.Headers.Add("X-Twig-Bench", "1");
                if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                text.ShouldNotBeNullOrWhiteSpace(path);
                _linkHttpOutput?.WriteLine($"{path}: {(int)response.StatusCode}, {elapsed.ElapsedMilliseconds}ms");
                return ((int)response.StatusCode, JsonNode.Parse(text)!);
            }
            async Task<JsonNode> Send(string path, JsonNode? body = null, HttpMethod? method = null)
            {
                var response = await Request(path, body, method);
                response.Status.ShouldBeInRange(200, 299, $"{path}: {response.Body}");
                return response.Body;
            }
            await Send("/context", new JsonObject { ["root"] = fixture.Root }, HttpMethod.Put);
            var view = await Send($"/benches/{fixture.Bench:D}", method: HttpMethod.Get);
            var source = view["sources"]!.AsArray().Single()!;
            source["items"]!.AsArray().Count.ShouldBe(2);
            source["provider"]!.GetValue<string>().ShouldBe("github");
            var target = source["items"]!.AsArray().Single(item => item!["locator"]!.GetValue<string>() == "42")!;
            var other = source["items"]!.AsArray().Single(item => item!["locator"]!.GetValue<string>() == "43")!;
            var open = new JsonObject
            {
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["benchId"] = fixture.Bench.ToString("D"),
                ["itemId"] = target["id"]!.DeepClone(), ["planMode"] = false,
            };
            var scalar = await Send("/editor", open);
            await native.AssertStateAsync(0).WaitAsync(ct);
            var pair = await Send("/editor/relationships", new JsonObject
            {
                ["ticket"] = scalar["ticket"]!.DeepClone(), ["endpointId"] = other["id"]!.DeepClone(),
            });
            pair["localOnly"]!.GetValue<bool>().ShouldBeTrue();
            pair["relations"]!.AsArray().Count.ShouldBe(1);
            pair["relations"]![0]!["shape"]!.GetValue<string>().ShouldBe("Association");
            pair["relations"]![0]!["directed"]!.GetValue<bool>().ShouldBeFalse();
            pair["fields"]!["fields"]!.AsArray().All(field => field!["readOnly"]!.GetValue<bool>()).ShouldBeTrue();
            static JsonObject ReviewBody(JsonNode selected, bool adding = true) => new()
            {
                ["ticket"] = selected["fields"]!["ticket"]!.DeepClone(), ["adding"] = adding,
            };
            static JsonObject Reference(JsonNode review) => new()
            {
                ["ticket"] = review["ticket"]!.DeepClone(), ["captureId"] = review["captureId"]!.DeepClone(),
                ["digest"] = review["digest"]!.DeepClone(), ["operation"] = review["outcome"]!["operation"]!.DeepClone(),
                ["attempt"] = review["outcome"]!["attempt"]!.DeepClone(),
            };
            var readBody = new JsonObject
            {
                ["sourceId"] = source["sourceId"]!.DeepClone(), ["benchId"] = fixture.Bench.ToString("D"),
                ["itemId"] = target["id"]!.DeepClone(),
            };
            async Task<JsonNode> Rediscover(JsonNode original)
            {
                var result = (await Send("/editor/local/reviews", readBody)).AsArray().Single(value =>
                    value!["captureId"]!.GetValue<string>() == original["captureId"]!.GetValue<string>())!;
                result["canonicalProposal"]!.GetValue<string>().ShouldBe(original["canonicalProposal"]!.GetValue<string>());
                result["digest"]!.GetValue<string>().ShouldBe(original["digest"]!.GetValue<string>());
                await native.AssertCanonicalAsync(Guid.Parse(result["captureId"]!.GetValue<string>()),
                    result["canonicalProposal"]!.GetValue<string>(), result["digest"]!.GetValue<string>()).WaitAsync(ct);
                return result;
            }
            if (scenario == "counterfactual")
            {
                await native.AssertStateAsync(0).WaitAsync(ct);
            }
            else if (scenario == "guards")
            {
                var pristine = await native.PreserveAsync().WaitAsync(ct);
                var requests = native.Human.Native.Http.Requests;
                foreach (var path in new[] { "reviews", "review", "apply", "observe", "continue" })
                    (await Request("/editor/local/" + path, new JsonObject(), capability: canvas.PresentationCapability))
                        .Status.ShouldBe(403, "SDK remains presentation-only: " + path);
                (await Request("/editor/local/review", ReviewBody(pair), csrf: false)).Status.ShouldBe(403);
                native.Human.Native.Http.Requests.ShouldBe(requests, "SDK/CSRF gates perform zero provider HTTP.");
                foreach (var unavailable in new[] { target["id"]!.GetValue<string>(), new string('0', 64) })
                    (await Request("/editor/relationships", new JsonObject
                    {
                        ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(), ["endpointId"] = unavailable,
                    })).Status.ShouldBe(404, "Self/unknown endpoint cannot be inferred or rebound.");
                foreach (var invalid in new[]
                {
                    """{"ticket":null,"adding":true}""", """{"adding":true}""",
                    $$"""{"ticket":"{{pair["fields"]!["ticket"]!.GetValue<string>()}}","adding":null}""",
                    $$"""{"ticket":"{{pair["fields"]!["ticket"]!.GetValue<string>()}}","adding":true,"fields":[]}""",
                })
                    (await Request("/editor/local/review", JsonNode.Parse(invalid))).Status.ShouldBe(400);
                foreach (var path in new[] { "/editor/relationships/stage", "/editor/relationships/save", "/editor/save" })
                    (await Request(path, new JsonObject
                    {
                        ["ticket"] = pair["fields"]!["ticket"]!.DeepClone(), ["fields"] = new JsonArray(),
                        ["intent"] = new JsonArray(new JsonObject
                        {
                            ["endpointId"] = other["id"]!.DeepClone(),
                            ["relation"] = pair["relations"]![0]!["reference"]!.DeepClone(),
                            ["outgoing"] = true, ["remove"] = false,
                        }),
                    })).Status.ShouldBeGreaterThanOrEqualTo(400, "LOCAL cannot use combined scalar/ADO routes.");
                await pristine().WaitAsync(ct);
                var review = await Send("/editor/local/review", ReviewBody(pair));
                var preserved = await native.PreserveAsync().WaitAsync(ct);
                var wrong = Reference(review); wrong["digest"] = new string('0', 64);
                (await Request("/editor/local/apply", wrong)).Status.ShouldBe(409);
                wrong = Reference(review); wrong["attempt"] = Guid.NewGuid().ToString("D");
                (await Request("/editor/local/observe", wrong)).Status.ShouldBe(409);
                wrong = Reference(review); wrong["operation"] = 1;
                (await Request("/editor/local/apply", wrong)).Status.ShouldBe(400);
                (await Request("/editor/local/review", ReviewBody(pair))).Status.ShouldBe(409);
                await Send("/editor/close", new JsonObject { ["ticket"] = review["ticket"]!.DeepClone() });
                (await Request("/editor/local/apply", Reference(review))).Status.ShouldBe(409);
                await preserved().WaitAsync(ct);
            }
            else
            {
                var review = await Send("/editor/local/review", ReviewBody(pair));
                review["canApply"]!.GetValue<bool>().ShouldBeTrue();
                review["outcome"]!["status"]!.GetValue<string>().ShouldBe("Planned");
                if (scenario.StartsWith("binding-", StringComparison.Ordinal))
                {
                    var damage = scenario[8..];
                    var originalReference = Reference(review);
                    if (damage == "generation")
                    {
                        // Public sign-out rotates the real durable profile, then an explicitly reconnected owner restores the exact source.
                        await Send("/session", method: HttpMethod.Delete);
                        human = sessions.ConnectOwner(instance, canvas.ViewId, profile);
                        owned = sessions.Content(human.ViewId, human.ContentCapability, true);
                        await Send("/context", new JsonObject { ["root"] = fixture.Root }, HttpMethod.Put);
                        var restored = await Send($"/benches/{fixture.Bench:D}", method: HttpMethod.Get);
                        restored["sources"]![0]!["sourceId"]!.GetValue<string>().ShouldBe(source["sourceId"]!.GetValue<string>());
                        (await Request("/session", method: HttpMethod.Get)).Status.ShouldBe(200,
                            "The refusal below must use a fresh valid owner, not a stale bearer capability.");
                    }
                    else await native.DamageAsync(damage).WaitAsync(ct);
                    var preserved = await native.PreserveAsync().WaitAsync(ct);
                    var refused = await Request("/editor/local/apply", originalReference);
                    if (damage == "generation") refused.Status.ShouldBe(409, "Original reviewed ticket cannot survive real profile retirement.");
                    else (refused.Status >= 400 || refused.Body["outcome"]!["status"]!.GetValue<string>() != "Verified")
                        .ShouldBeTrue("Changed " + damage + " cannot apply.");
                    await preserved().WaitAsync(ct);
                }
                else if (scenario == "protected")
                {
                    native.ArmUnknown();
                    var result = await Send("/editor/local/apply", Reference(review));
                    result["outcome"]!["status"]!.GetValue<string>().ShouldBe("Applying");
                    result["canApply"]!.GetValue<bool>().ShouldBeFalse();
                    result["outcome"]!["error"].ShouldNotBeNull();
                    await native.AssertProtectedAsync().WaitAsync(ct);
                    var preserved = await native.PreserveAsync().WaitAsync(ct);
                    review = await Rediscover(result);
                    (await Request("/editor/local/apply", Reference(review))).Status.ShouldBe(409);
                    await Send("/editor/local/observe", Reference(review));
                    await preserved().WaitAsync(ct);
                }
                else if (scenario == "delivery")
                {
                    native.ArmApplied();
                    JsonNode applied;
                    try { applied = await Send("/editor/local/apply", Reference(review)); }
                    finally { native.ReleaseApplied(); }
                    applied["outcome"]!["status"]!.GetValue<string>().ShouldBe("Applied");
                    applied["canContinue"]!.GetValue<bool>().ShouldBeTrue();
                    applied["canApply"]!.GetValue<bool>().ShouldBeFalse();
                    review = await Rediscover(applied);
                    var continued = await Send("/editor/local/continue", Reference(review));
                    continued["outcome"]!["status"]!.GetValue<string>().ShouldBe("Verified");
                    continued["outcome"]!["settled"]!.GetValue<bool>().ShouldBeTrue();
                    await native.AssertContinuedAsync().WaitAsync(ct);
                }
                else
                {
                    scenario.ShouldBe("lifecycle");
                    await Send("/editor/close", new JsonObject { ["ticket"] = review["ticket"]!.DeepClone() });
                    review = await Rediscover(review);
                    var result = await Send("/editor/local/apply", Reference(review));
                    result["outcome"]!["status"]!.GetValue<string>().ShouldBe("Verified");
                    result["outcome"]!["settled"]!.GetValue<bool>().ShouldBeTrue();
                    result["canApply"]!.GetValue<bool>().ShouldBeFalse();
                    await native.AssertStateAsync(1).WaitAsync(ct);
                    var preserved = await native.PreserveAsync().WaitAsync(ct);
                    (await Request("/editor/local/apply", Reference(result))).Status.ShouldBe(409);
                    result = await Rediscover(result);
                    await Send("/editor/local/observe", Reference(result));
                    await preserved().WaitAsync(ct);
                    scalar = await Send("/editor", new JsonObject
                    {
                        ["sourceId"] = source["sourceId"]!.DeepClone(), ["benchId"] = fixture.Bench.ToString("D"),
                        ["itemId"] = other["id"]!.DeepClone(), ["planMode"] = false,
                    });
                    pair = await Send("/editor/relationships", new JsonObject
                    {
                        ["ticket"] = scalar["ticket"]!.DeepClone(), ["endpointId"] = target["id"]!.DeepClone(),
                    });
                    pair["incident"]![0]!["localVersion"]!.GetValue<long>().ShouldBe(1);
                    review = await Send("/editor/local/review", ReviewBody(pair, false));
                    review["effect"]!["expectedLocalVersion"]!.GetValue<long>().ShouldBe(1);
                    await Send("/editor/local/apply", Reference(review));
                    await native.AssertStateAsync(2).WaitAsync(ct);
                }
            }
            native.Human.Writes.ShouldBe(0, "LOCAL never publishes work-item relationships or any provider mutation.");
            native.Human.Native.Http.Requests.ShouldBeGreaterThan(0, "Native metadata/item acquisition used actual synthetic HTTP.");
            var final = await native.Human.SnapshotAsync().WaitAsync(ct);
            _linkHttpOutput?.WriteLine($"GITHUB LOCAL HTTP {scenario}: providerHttp={native.Human.Native.Http.Requests}, " +
                $"providerWrites={native.Human.Writes}, localVersion={final.Relationships.SingleOrDefault()?.LocalVersion ?? 0}, " +
                $"captures={(await store.ReadEditorCapturesAsync(ct)).Value.Length}, proposals={final.Proposals.Length}, " +
                $"journal={final.Journal.Length}, receipts={final.Journal.Sum(row => row.Receipts.Length)}");
        }
        finally
        {
            native.ReleaseApplied();
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }
}
