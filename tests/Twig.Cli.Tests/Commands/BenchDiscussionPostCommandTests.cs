using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class BenchDiscussionPostCommandTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task ProductionParserPreparesExactNativeProposalWithoutPostingOrChangingState()
    {
        var (runtime, input) = await SetupAsync();
        input["text"] = "<script>not HTML</script>\n\u03a9";
        var before = BenchDiscussionParserBaselineTests.Snapshot(runtime, _fixture);
        var http = _fixture.Transport.SnapshotHttp();
        var result = Run(runtime, input.ToJsonString());
        result.Exit.ShouldBe(0, result.Error);
        result.Out.ShouldStartWith("{");
        using var output = JsonDocument.Parse(result.Out);
        var root = output.RootElement;
        root.GetProperty("status").GetString().ShouldBe("prepared");
        root.GetProperty("context").GetRawText().ShouldNotBe("null");
        var parsed = new PlanDocumentParser().Parse(root.GetProperty("proposal").GetString()!);
        parsed.IsValid.ShouldBeTrue();
        parsed.Digest.ShouldBe(root.GetProperty("digest").GetString());
        var operation = parsed.Plan!.Operations.Single().ShouldBeOfType<Twig.Domain.Services.Plan.AppendCommentOperation>();
        operation.WorkItemId.ShouldBe(42);
        operation.Text.ShouldBe(input["text"]!.GetValue<string>());
        operation.EffectiveBody.ShouldBe(DiscussionPostBodyFormatter.Format("plain", operation.Text));
        operation.EffectiveBody.ShouldNotContain("<script>");
        _fixture.Transport.SnapshotHttp().ShouldBe(http);
        BenchDiscussionParserBaselineTests.Snapshot(runtime, _fixture).ShouldBe(before);
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task ProductionParserRefusesStaleContextAndMalformedInputWithoutPosting()
    {
        var (runtime, input) = await SetupAsync();
        input["context"]!["benchContentsDigest"] = new string('0', 64);
        var http = _fixture.Transport.SnapshotHttp();
        var stale = Run(runtime, input.ToJsonString());
        stale.Exit.ShouldBe(1, stale.Out);
        using var output = JsonDocument.Parse(stale.Out);
        output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("authority-invalidated");
        output.RootElement.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        foreach (var invalid in new[] { "{}", "{\"version\":1,\"version\":1}", "{invalid" })
        {
            var result = Run(runtime, invalid);
            result.Exit.ShouldBe(2, result.Out);
            using var error = JsonDocument.Parse(result.Error);
            error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        _fixture.Transport.SnapshotHttp().ShouldBe(http);
    }

    [HostMigrationFact]
    public async Task ProductionParserHasHelpFormatsLimitsAndOnlyAllowlistedTelemetry()
    {
        var (runtime, input) = await SetupAsync();
        var http = _fixture.Transport.SnapshotHttp();
        var help = Run(runtime, "", "json", help: true);
        help.Exit.ShouldBe(0);
        help.Out.ShouldContain("--stdin");
        CommandExamples.Examples["bench discussion prepare-post"].Length.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var format in new[] { "human", "minimal" })
        {
            var result = Run(runtime, input.ToJsonString(), format);
            result.Exit.ShouldBe(0, result.Error);
            result.Out.ShouldContain("digest\t");
            result.Out.ShouldContain("proposal\t");
        }
        Run(runtime, input.ToJsonString(), "ids").Exit.ShouldBe(2);
        var telemetry = Substitute.For<ITelemetryClient>();
        input["format"] = "markdown";
        input["text"] = "Hello **team**";
        Run(runtime, input.ToJsonString(), telemetry: telemetry).Exit.ShouldBe(0);
        var call = telemetry.ReceivedCalls().Single();
        var properties = (Dictionary<string, string>)call.GetArguments()[1]!;
        properties["command"].ShouldBe("bench discussion prepare-post");
        properties.Keys.ShouldAllBe(key => new[] { "command", "exit_code", "output_format", "twig_version", "os_platform", "operation_id" }.Contains(key));
        ((Dictionary<string, double>)call.GetArguments()[2]!).Keys.ShouldBe(["duration_ms"]);
        foreach (var text in new[] { new string('a', 16385), "![image](https://example.invalid/x.png)", "[unsafe](javascript:alert(1))" })
        {
            input["text"] = text;
            Run(runtime, input.ToJsonString()).Exit.ShouldBe(2);
        }
        input["context"] = null;
        Run(runtime, input.ToJsonString()).Exit.ShouldBe(2);
        _fixture.Transport.SnapshotHttp().ShouldBe(http);
    }

    [HostMigrationFact]
    public async Task PreparedProposalUsesRealLifecycleSinglePostAndTypedCommentIdReadback()
    {
        using var handler = new CommentHttp(_fixture.Transport);
        using var http = new HttpClient(handler);
        var (runtime, input) = await SetupAsync(http);
        var prepared = Run(runtime, input.ToJsonString());
        prepared.Exit.ShouldBe(0, prepared.Error);
        using var output = JsonDocument.Parse(prepared.Out);
        var root = output.RootElement;
        var file = Path.Combine(_fixture.Paths.StartDir, "discussion-prepared.json");
        await File.WriteAllTextAsync(file, root.GetProperty("proposal").GetString());
        var digest = root.GetProperty("digest").GetString()!;
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var preview = await lifecycle.PreviewAsync(file, default);
        preview.CanApply.ShouldBeTrue();
        preview.Digest.ShouldBe(digest);
        var result = await lifecycle.ApplyAsync(file, digest, default);
        handler.Posts.ShouldBe(1, "Captured HTTP: " + string.Join("; ", handler.Requests));
        result.Failed.ShouldBeFalse(result.Error + " " + string.Join("; ", result.Operations.Select(row => row.State + ": " + row.Error)));
        result.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        handler.Posts.ShouldBe(1);
        handler.Reads.ShouldBe(["2147483647"]);
        using var receipt = JsonDocument.Parse(result.Operations.Single().ResultJson!);
        receipt.RootElement.GetProperty("commentId").GetString().ShouldBe("2147483647");
        (await lifecycle.ApplyAsync(file, digest, default)).Failed.ShouldBeFalse();
        handler.Posts.ShouldBe(1, "An already verified native append is never posted again.");
    }

    [HostMigrationFact]
    public async Task LostPostAcknowledgmentRemainsUnknownWithoutListInferenceOrRetry()
    {
        using var handler = new CommentHttp(_fixture.Transport, loseAcknowledgment: true);
        using var http = new HttpClient(handler);
        var (runtime, input) = await SetupAsync(http);
        var prepared = Run(runtime, input.ToJsonString());
        prepared.Exit.ShouldBe(0, prepared.Error);
        using var output = JsonDocument.Parse(prepared.Out);
        var file = Path.Combine(_fixture.Paths.StartDir, "discussion-unknown.json");
        await File.WriteAllTextAsync(file, output.RootElement.GetProperty("proposal").GetString());
        var digest = output.RootElement.GetProperty("digest").GetString()!;
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        (await lifecycle.PreviewAsync(file, default)).CanApply.ShouldBeTrue();
        foreach (var _ in new[] { 1, 2 })
        {
            var result = await lifecycle.ApplyAsync(file, digest, default);
            result.Failed.ShouldBeTrue();
            result.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
            result.Operations.Single().Error.ShouldNotBeNull();
            result.Operations.Single().Error!.ShouldContain("No attributable comment receipt");
        }
        handler.Posts.ShouldBe(1);
        handler.Reads.ShouldBeEmpty();
    }

    private sealed class CommentHttp(HttpMessageHandler fallback, bool loseAcknowledgment = false) : HttpMessageHandler
    {
        internal int Posts { get; private set; }
        internal List<string> Reads { get; } = [];
        internal List<string> Requests { get; } = [];
        private string? _body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/42/comments", StringComparison.Ordinal))
            {
                Posts++;
                using var posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                _body = posted.RootElement.GetProperty("text").GetString();
                if (loseAcknowledgment) throw new HttpRequestException("Acknowledgment lost after POST.");
            }
            else if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/42/comments/2147483647", StringComparison.Ordinal))
                Reads.Add("2147483647");
            else
            {
                using var invoker = new HttpMessageInvoker(fallback, disposeHandler: false);
                return await invoker.SendAsync(request, ct);
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["id"] = int.MaxValue, ["workItemId"] = 42, ["text"] = _body, ["format"] = "html", ["isDeleted"] = false,
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private async Task<(ServiceProvider Runtime, JsonObject Input)> SetupAsync(HttpClient? http = null)
    {
        var runtime = await _fixture.CreateRuntimeAsync(http: http);
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Explicit discussion target"));
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = (await benches.CreateAsync("Post target"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        var capture = (await runtime.GetRequiredService<BrowserOperationModule>().ExecuteExplicitAsync(
            bench.Name, (value, _) => Task.FromResult(value))).Value!;
        var key = new JsonObject
        {
            ["checkoutRoot"] = capture.Origin.WorktreeRoot, ["connectionId"] = capture.Origin.Binding.ConnectionRef,
            ["bindingId"] = capture.BindingId, ["workItemId"] = 42,
        };
        var context = new JsonObject
        {
            ["itemKey"] = key, ["benchId"] = capture.BenchId, ["benchName"] = capture.BenchName,
            ["benchContentsDigest"] = capture.BenchContentsDigest, ["identityId"] = capture.IdentityId,
            ["bindingRevision"] = capture.BindingRevision.ToString(CultureInfo.InvariantCulture),
            ["selectionSource"] = capture.SelectionSource,
            ["selectionRevision"] = capture.SelectionRevision.ToString(CultureInfo.InvariantCulture),
            ["attachmentRevision"] = capture.Origin.Operation.AttachmentRevision.ToString(CultureInfo.InvariantCulture),
            ["storageGeneration"] = capture.StorageGeneration,
        };
        return (runtime, new JsonObject { ["version"] = 1, ["context"] = context, ["format"] = "plain", ["text"] = "Exact body" });
    }

    private static (int Exit, string Out, string Error) Run(ServiceProvider runtime, string input,
        string format = "json", ITelemetryClient? telemetry = null, bool help = false)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input));
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var args = help ? new[] { "bench", "discussion", "prepare-post", "--help" } :
                new[] { "bench", "discussion", "prepare-post", "--stdin", "-o", format };
            var exit = Twig.TwigConsoleApp.Run(args, services =>
            {
                services.AddTwigCommands();
                services.AddSingleton(runtime.GetRequiredService<CommandContext>() with { Stderr = error, TelemetryClient = telemetry });
                services.AddSingleton(runtime.GetRequiredService<BrowserOperationModule>());
                services.AddSingleton(runtime.GetRequiredService<BenchEvaluator>());
                services.AddSingleton(runtime.GetRequiredService<IWorkItemRepository>());
                services.AddSingleton<Stream>(stdin);
            });
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
