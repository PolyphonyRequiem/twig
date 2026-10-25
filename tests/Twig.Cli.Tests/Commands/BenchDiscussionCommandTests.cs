using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

// This class can be copied unchanged to the baseline with only the mechanical parser-factory overlay.
[Trait("Category", "HostInventory")]
public sealed class BenchDiscussionParserBaselineTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task RealGeneratedParserEmitsDiscussionUsageWithoutWrites()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var before = Snapshot(runtime, _fixture);
        var httpBefore = _fixture.Transport.SnapshotHttp();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = Twig.TwigConsoleApp.Run(["bench", "discussion", "-o", "json"], services =>
            {
                services.AddTwigCommands();
                services.AddSingleton(runtime.GetRequiredService<CommandContext>() with { Stderr = error });
            });
            exit.ShouldBe(2, $"stdout={output}\nstderr={error}");
            using var parsed = JsonDocument.Parse(output.ToString());
            parsed.RootElement.GetProperty("status").GetString().ShouldBe("refused");
            parsed.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("usage");
            parsed.RootElement.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
            parsed.RootElement.GetProperty("comments").GetArrayLength().ShouldBe(0);
            using var stderr = JsonDocument.Parse(error.ToString());
            stderr.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        _fixture.Transport.SnapshotHttp().ShouldBe(httpBefore);
        Snapshot(runtime, _fixture).ShouldBe(before);
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
    }

    internal static string Snapshot(ServiceProvider runtime, NativeBrowserFixture fixture)
    {
        using var operation = runtime.GetRequiredService<IWorkItemRepository>().AcquireOperation();
        var connection = runtime.GetRequiredService<SqliteCacheStore>().GetConnection();
        var result = new StringBuilder();
        foreach (var schema in new[] { "main", "pending" })
        {
            using var names = connection.CreateCommand();
            names.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table' ORDER BY name;";
            var tables = new List<string>();
            using (var rows = names.ExecuteReader()) while (rows.Read()) tables.Add(rows.GetString(0));
            foreach (var table in tables)
            {
                result.Append(schema).Append('.').AppendLine(table);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {schema}.\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                var values = new List<string>();
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    var row = new StringBuilder();
                    for (var i = 0; i < rows.FieldCount; i++)
                    {
                        var value = rows.GetValue(i);
                        var cell = value is DBNull ? "<null>" : value is byte[] bytes ? Convert.ToHexString(bytes)
                            : Convert.ToString(value, CultureInfo.InvariantCulture)!;
                        row.Append(cell.Length).Append(':').Append(cell);
                    }
                    values.Add(row.ToString());
                }
                foreach (var value in values.Order(StringComparer.Ordinal)) result.AppendLine(value);
            }
        }
        foreach (var root in new[] { fixture.Home, fixture.Paths.TwigDir })
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                result.Append(file).Append(':').AppendLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        return result.ToString();
    }
}

[Trait("Category", "HostInventory")]
public sealed class BenchDiscussionCommandTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task RealGeneratedParserBindsStdinAndEmitsStructuredJsonRefusal()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        foreach (var input in new[] { "{invalid", "{}" })
        {
            var result = Run(runtime, ["bench", "discussion", "--stdin", "-o", "json"], input,
                () => throw new InvalidOperationException("Invalid input must not resolve the native graph."));
            result.Exit.ShouldBe(2, result.Error);
            using var parsed = JsonDocument.Parse(result.Out);
            parsed.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid-json");
            using var stderr = JsonDocument.Parse(result.Error);
            stderr.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        _fixture.Transport.WorkReadAttempts.ShouldBe(0);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RealGeneratedParserReturnsOneAdmittedPageAndPreservesNativeState()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var cold = _fixture.Transport.SnapshotHttp();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Cached title"));
        var warm = _fixture.Transport.SnapshotHttp();
        warm.ShouldBe(cold with { Total = cold.Total + 2, Principal = cold.Principal + 2 },
            "First cached-item projection owns PAT attestation and the resource-scope GET.");
        _ = await runtime.GetRequiredService<IAuthenticationProvider>().GetAccessTokenAsync();
        _fixture.Transport.SnapshotHttp().ShouldBe(warm, "The shared native authentication provider is already warm.");
        await repository.SaveAsync(NativeBrowserFixture.Item(99, "Keep active"));
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(99);
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(99, "note", null, null, "Keep pending");
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var target = (await benches.CreateAsync("Explicit discussion"))!;
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForItem(42));
        var current = (await benches.CreateAsync("Keep current"))!;
        await benches.SetCurrentAsync(current.Id);
        var origin = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        var request = new DiscussionRequest(1,
            new(origin.WorktreeRoot, origin.Binding.ConnectionRef, origin.Binding.BindingId, 42),
            target.Name, target.Id.ToString(CultureInfo.InvariantCulture), origin.Identity.IdentityId, null);
        using var handler = new ParserDiscussionHttp();
        using var http = new HttpClient(handler);
        var before = BenchDiscussionParserBaselineTests.Snapshot(runtime, _fixture);
        var httpBefore = _fixture.Transport.SnapshotHttp();
        var originalExit = Environment.ExitCode;
        var result = Run(runtime, ["bench", "discussion", "--stdin", "-o", "json"],
            JsonSerializer.Serialize(request, TwigJsonContext.Default.DiscussionRequest), () => new(
                runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<BenchEvaluator>(), repository,
                new AdoRestClient(http, runtime.GetRequiredService<IAuthenticationProvider>(),
                    "https://dev.azure.com/fixture", NativeBrowserFixture.Project, new WorkItemMapper())));
        Environment.ExitCode.ShouldBe(originalExit);
        result.Exit.ShouldBe(0, result.Error);
        result.Error.ShouldBeEmpty();
        using var parsed = JsonDocument.Parse(result.Out);
        parsed.RootElement.GetProperty("status").GetString().ShouldBe("read");
        parsed.RootElement.GetProperty("comments")[0].GetProperty("id").GetString().ShouldBe("45");
        parsed.RootElement.GetProperty("comments")[0].GetProperty("isDeleted").ValueKind.ShouldBe(JsonValueKind.Null);
        parsed.RootElement.GetProperty("context").GetProperty("benchId").GetString().ShouldBe(request.ExpectBench);
        parsed.RootElement.GetProperty("paging").GetProperty("traversal").GetString().ShouldBe("exhausted");
        handler.Calls.ShouldBe(1);
        handler.Method.ShouldBe(HttpMethod.Get);
        handler.Uri!.AbsolutePath.ShouldBe("/fixture/Work/_apis/wit/workItems/42/comments");
        BenchDiscussionParserBaselineTests.Snapshot(runtime, _fixture).ShouldBe(before);
        _fixture.Transport.SnapshotHttp().ShouldBe(httpBefore,
            "Setup owns cold attestation and scope discovery; the admitted page adds no fixture HTTP.");
    }

    [HostMigrationFact]
    public async Task RealHelpIsReachableAndIdsRefusalIsUsage()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var help = Run(runtime, ["bench", "discussion", "--help"], "", () => throw new InvalidOperationException());
        help.Exit.ShouldBe(0, help.Error);
        help.Out.ShouldContain("--stdin");
        var ids = Run(runtime, ["bench", "discussion", "-o", "ids"], "", () => throw new InvalidOperationException());
        ids.Exit.ShouldBe(2);
        using var error = JsonDocument.Parse(ids.Error);
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("not ids");
        CommandExamples.Examples["bench discussion"].Length.ShouldBeGreaterThanOrEqualTo(2);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    private static (int Exit, string Out, string Error) Run(ServiceProvider runtime, string[] args, string input,
        Func<BrowserDiscussionModule> module)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input));
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var context = runtime.GetRequiredService<CommandContext>() with { Stderr = error };
            var exit = Twig.TwigConsoleApp.Run(args, services =>
                services.AddSingleton<BenchDiscussionCommand>(_ => new(context, module, output, stdin)));
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class ParserDiscussionHttp : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal HttpMethod? Method { get; private set; }
        internal Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"count":1,"comments":[{"id":45,"text":"one page","format":"markdown"}]}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }
}
