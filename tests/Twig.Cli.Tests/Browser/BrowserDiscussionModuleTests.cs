using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserDiscussionModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task TwoPagesUseFreshAdmissionWithoutAnyNativeDataMutation()
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp();
        using var http = new HttpClient(handler);
        var module = Module(runtime, http);
        var before = Snapshot(runtime);
        var authBefore = _fixture.Transport.SnapshotHttp();
        var first = await module.ReadAsync(request);
        first.Status.ShouldBe("read");
        first.Comments.ShouldHaveSingleItem().Id.ShouldBe("45");
        first.Context!.ItemKey.ShouldBe(request.ItemKey);
        first.Paging!.NextCursor.ShouldNotBeNull();
        first.Paging.Traversal.ShouldBe("more");
        first.Paging.SnapshotConsistency.ShouldBe("not-guaranteed");
        Snapshot(runtime).ShouldBe(before);
        var afterFirst = _fixture.Transport.SnapshotHttp();
        afterFirst.ShouldBe(authBefore,
            "Setup owns cold attestation and scope discovery; both pages reuse the proven warm native provider.");
        var second = await module.ReadAsync(request with { Cursor = first.Paging.NextCursor });
        second.Status.ShouldBe("read");
        second.Paging!.Traversal.ShouldBe("exhausted");
        second.Paging.NextCursor.ShouldBeNull();
        second.Comments.ShouldBeEmpty();
        Snapshot(runtime).ShouldBe(before);
        _fixture.Transport.SnapshotHttp().ShouldBe(afterFirst);
        handler.Uris.Count.ShouldBe(2);
        handler.Uris[1].Query.ShouldContain("continuationToken=opaque%2Bnext%26value");
        handler.Methods.ShouldAllBe(method => method == HttpMethod.Get);
    }

    [HostMigrationFact]
    public async Task EchoedContextDoesNotAuthenticatePagingDataOrChooseTheRequestDestination()
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp();
        using var http = new HttpClient(handler);
        var module = Module(runtime, http);
        var first = await module.ReadAsync(request);
        first.Status.ShouldBe("read");
        var cursor = first.Paging!.NextCursor! with
        {
            ProviderToken = "https://evil.invalid/99?credential=other&workItemId=99",
        };
        var before = Snapshot(runtime);
        (await module.ReadAsync(request with { Cursor = cursor })).Status.ShouldBe("read");
        handler.Uris.Count.ShouldBe(2);
        handler.Uris[1].Host.ShouldBe("dev.azure.com");
        handler.Uris[1].AbsolutePath.ShouldBe("/fixture/Work/_apis/wit/workItems/42/comments");
        handler.Uris[1].Query.ShouldContain("continuationToken=" + Uri.EscapeDataString(cursor.ProviderToken));
        Snapshot(runtime).ShouldBe(before);
    }

    [HostMigrationFact]
    public async Task ChangedNativeBenchInvalidatesAnOldCursorBeforeAnotherPageIsRequested()
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp();
        using var http = new HttpClient(handler);
        var module = Module(runtime, http);
        var first = await module.ReadAsync(request);
        first.Status.ShouldBe("read");
        await runtime.GetRequiredService<IBenchRepository>().AddSelectorAsync(
            long.Parse(request.ExpectBench, CultureInfo.InvariantCulture), BenchSelector.ForItem(100));
        var before = Snapshot(runtime);
        var result = await module.ReadAsync(request with { Cursor = first.Paging!.NextCursor });
        Refused(result);
        result.Error!.Code.ShouldBe("authority-invalidated");
        handler.Uris.Count.ShouldBe(1);
        Snapshot(runtime).ShouldBe(before);
    }

    [HostMigrationTheory]
    [InlineData("checkout")]
    [InlineData("connection")]
    [InlineData("binding")]
    [InlineData("identity")]
    [InlineData("bench-id")]
    [InlineData("not-member")]
    [InlineData("not-cached")]
    [InlineData("missing-bench")]
    public async Task ExplicitAdmissionRefusesBeforeDiscussionHttpAndLeavesNativeDataUntouched(string fault)
    {
        var (runtime, request) = await PrepareAsync();
        if (fault == "not-cached") await runtime.GetRequiredService<IWorkItemRepository>().DeleteByIdAsync(42);
        request = fault switch
        {
            "checkout" => request with { ItemKey = new(request.ItemKey.CheckoutRoot + "-other", request.ItemKey.ConnectionId, request.ItemKey.BindingId, 42) },
            "connection" => request with { ItemKey = new(request.ItemKey.CheckoutRoot, "other", request.ItemKey.BindingId, 42) },
            "binding" => request with { ItemKey = new(request.ItemKey.CheckoutRoot, request.ItemKey.ConnectionId, "other", 42) },
            "identity" => request with { ExpectIdentity = "other" },
            "bench-id" => request with { ExpectBench = "9223372036854775807" },
            "not-member" => request with { ItemKey = new(request.ItemKey.CheckoutRoot, request.ItemKey.ConnectionId, request.ItemKey.BindingId, 123) },
            "missing-bench" => request with { Bench = "absent" },
            _ => request,
        };
        using var handler = new DiscussionHttp();
        using var http = new HttpClient(handler);
        var before = Snapshot(runtime);
        var result = await Module(runtime, http).ReadAsync(request);
        Refused(result);
        if (fault is "not-member" or "not-cached")
            result.Error!.Code.ShouldBe(fault == "not-member" ? "bench-item-not-member" : "bench-item-not-cached");
        handler.Uris.ShouldBeEmpty();
        Snapshot(runtime).ShouldBe(before);
        _fixture.Transport.WorkReadAttempts.ShouldBe(0);
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
        _fixture.Transport.MetadataAttempts.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("item")]
    [InlineData("bench")]
    [InlineData("identity")]
    [InlineData("binding-revision")]
    [InlineData("selection-revision")]
    [InlineData("attachment-revision")]
    [InlineData("storage-generation")]
    [InlineData("profile")]
    public async Task CursorContextIsMismatchDetectionNotAuthority(string fault)
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp();
        using var http = new HttpClient(handler);
        var module = Module(runtime, http);
        var first = await module.ReadAsync(request);
        first.Status.ShouldBe("read");
        var cursor = first.Paging!.NextCursor!;
        cursor = fault switch
        {
            "item" => cursor with { Context = cursor.Context with { ItemKey = new(request.ItemKey.CheckoutRoot, request.ItemKey.ConnectionId, request.ItemKey.BindingId, 99) } },
            "bench" => cursor with { Context = cursor.Context with { BenchContentsDigest = "tampered" } },
            "identity" => cursor with { Context = cursor.Context with { IdentityId = "tampered" } },
            "binding-revision" => cursor with { Context = cursor.Context with { BindingRevision = "999" } },
            "selection-revision" => cursor with { Context = cursor.Context with { SelectionRevision = "999" } },
            "attachment-revision" => cursor with { Context = cursor.Context with { AttachmentRevision = "999" } },
            "storage-generation" => cursor with { Context = cursor.Context with { StorageGeneration = "tampered" } },
            _ => cursor with { OptionProfile = "other" },
        };
        var before = Snapshot(runtime);
        Refused(await module.ReadAsync(request with { Cursor = cursor }));
        handler.Uris.Count.ShouldBe(1);
        Snapshot(runtime).ShouldBe(before);
    }

    [HostMigrationTheory]
    [InlineData("bench")]
    [InlineData("origin")]
    [InlineData("cancel")]
    public async Task PostReadFenceSuppressesLateDataAndCursor(string change)
    {
        var (runtime, request) = await PrepareAsync();
        var authorityPath = _fixture.Paths.RepoConfigPath;
        File.Exists(authorityPath).ShouldBeTrue();
        var originalConfiguration = await File.ReadAllBytesAsync(authorityPath);
        var initialConfiguration = await TwigConfiguration.LoadSplitAsync(_fixture.Paths);
        initialConfiguration.IsLegacyMode.ShouldBeFalse();
        initialConfiguration.Project.ShouldBe(NativeBrowserFixture.Project);
        var callbackReached = false;
        using var cancellation = new CancellationTokenSource();
        using var handler = new DiscussionHttp
        {
            BeforeReply = async () =>
            {
                callbackReached = true;
                if (change == "bench")
                    await runtime.GetRequiredService<IBenchRepository>().AddSelectorAsync(long.Parse(request.ExpectBench, CultureInfo.InvariantCulture), BenchSelector.ForItem(100));
                else if (change == "origin")
                {
                    var changedConfiguration = await TwigConfiguration.LoadSplitAsync(_fixture.Paths);
                    changedConfiguration.IsLegacyMode.ShouldBeFalse();
                    changedConfiguration.Project.ShouldBe(NativeBrowserFixture.Project);
                    changedConfiguration.Project = "Changed";
                    await changedConfiguration.SaveRepoAsync(authorityPath);
                    var effectiveConfiguration = await TwigConfiguration.LoadSplitAsync(_fixture.Paths);
                    effectiveConfiguration.Project.ShouldBe("Changed");
                    effectiveConfiguration.Project.ShouldNotBe(initialConfiguration.Project);
                    _fixture.Configuration.Project.ShouldBe(NativeBrowserFixture.Project);
                }
                else cancellation.Cancel();
            },
        };
        using var http = new HttpClient(handler);
        try
        {
            var result = await Module(runtime, http).ReadAsync(request, cancellation.Token);
            callbackReached.ShouldBeTrue();
            Refused(result);
            result.Error!.Code.ShouldBe(change == "cancel" ? "canceled" : "authority-invalidated");
            handler.Uris.Count.ShouldBe(1);
        }
        finally
        {
            if (change == "origin") await File.WriteAllBytesAsync(authorityPath, originalConfiguration);
        }
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData(403, "discussion-denied")]
    [InlineData(404, "discussion-not-found")]
    [InlineData(429, "discussion-throttled")]
    [InlineData(200, "discussion-response-invalid")]
    [InlineData(-1, "discussion-offline")]
    [InlineData(-2, "discussion-offline")]
    [InlineData(-3, "discussion-offline")]
    public async Task ProviderErrorsExposeNoBodyTokenIdentityUrlOrPartialSuccess(int status, string code)
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp
        {
            Status = status < 0 ? HttpStatusCode.OK : (HttpStatusCode)status,
            Body = """{"message":"SECRET body https://private.invalid/ private@example.invalid token","comments":[{"id":1}]}""",
            BodyReadFailure = status switch
            {
                -1 => new IOException("SECRET https://private.invalid/token"),
                -2 => new HttpRequestException("SECRET https://private.invalid/token"),
                -3 => new TaskCanceledException("SECRET https://private.invalid/token"),
                _ => null,
            },
        };
        using var http = new HttpClient(handler);
        var before = Snapshot(runtime);
        var result = await Module(runtime, http).ReadAsync(request);
        Refused(result);
        result.Error!.Code.ShouldBe(code);
        var json = JsonSerializer.Serialize(result, TwigJsonContext.Default.DiscussionResult);
        foreach (var value in new[] { "SECRET", "private.invalid", "private@example", "token" }) json.ShouldNotContain(value);
        Snapshot(runtime).ShouldBe(before);
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("json")]
    public async Task CommandOutputsHonestFormatsAndDoesNotExecuteUntrustedBody(string format)
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp { Body = """{"count":1,"comments":[{"id":1,"text":"\u001b[31m[red]<script>body</script>"}]}""" };
        using var http = new HttpClient(handler);
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, TwigJsonContext.Default.DiscussionRequest));
        var output = new StringWriter();
        var stderr = new StringWriter();
        var command = new BenchDiscussionCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = stderr },
            () => Module(runtime, http), output, input);
        var before = Snapshot(runtime);
        (await command.ExecuteAsync(true, format)).ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
        if (format == "json")
        {
            using var parsed = JsonDocument.Parse(output.ToString());
            var comment = parsed.RootElement.GetProperty("comments")[0];
            comment.GetProperty("id").GetString().ShouldBe("1");
            foreach (var field in new[] { "version", "createdAt", "modifiedAt", "isDeleted", "format" })
                comment.GetProperty(field).ValueKind.ShouldBe(JsonValueKind.Null);
            comment.GetProperty("author").GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
            parsed.RootElement.GetProperty("paging").GetProperty("count").GetString().ShouldBe("1");
            parsed.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        else
        {
            output.ToString().ShouldNotContain("\u001b");
            output.ToString().ShouldContain("\\u001B[31m[red]\\u003Cscript\\u003Ebody\\u003C/script\\u003E");
            output.ToString().ShouldContain("snapshot-not-guaranteed");
        }
        Snapshot(runtime).ShouldBe(before);
    }

    [HostMigrationTheory]
    [InlineData("sqlite", "discussion-unavailable")]
    [InlineData("auth", "discussion-denied")]
    [InlineData("offline", "discussion-offline")]
    [InlineData("throttle", "discussion-throttled")]
    [InlineData("ado", "discussion-unavailable")]
    public async Task LazyGraphFailuresHaveFixedPrivateStructuredErrors(string kind, string code)
    {
        var (runtime, request) = await PrepareAsync();
        const string secret = "SECRET private@example.invalid https://private.invalid/token";
        Exception failure = kind switch
        {
            "sqlite" => new SqliteException(secret, 1),
            "auth" => new AdoAuthenticationException(secret),
            "offline" => new AdoOfflineException(new IOException(secret)),
            "throttle" => new AdoRateLimitException(TimeSpan.Zero),
            _ => new AdoException(secret),
        };
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, TwigJsonContext.Default.DiscussionRequest));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var command = new BenchDiscussionCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = stderr },
            () => throw failure, stdout, input);
        var before = Snapshot(runtime);
        var transportBefore = _fixture.Transport.SnapshotHttp();
        (await command.ExecuteAsync(true, "json")).ShouldBe(1);
        using var parsed = JsonDocument.Parse(stdout.ToString());
        parsed.RootElement.GetProperty("status").GetString().ShouldBe("refused");
        parsed.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        parsed.RootElement.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        parsed.RootElement.GetProperty("paging").ValueKind.ShouldBe(JsonValueKind.Null);
        parsed.RootElement.GetProperty("comments").GetArrayLength().ShouldBe(0);
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldBe("Discussion read refused (" + code + ").");
        foreach (var text in new[] { stdout.ToString(), stderr.ToString() })
            foreach (var value in new[] { "SECRET", "private@example", "private.invalid", "token" }) text.ShouldNotContain(value);
        Snapshot(runtime).ShouldBe(before);
        _fixture.Transport.SnapshotHttp().ShouldBe(transportBefore);
    }

    [HostMigrationTheory]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task TextFormatsDistinguishTombstoneUnknownEmptyAndLiteralNullWithoutTerminalControls(string format)
    {
        var (runtime, request) = await PrepareAsync();
        using var handler = new DiscussionHttp
        {
            Body = """
                {"count":3,"comments":[
                {"id":1,"isDeleted":true},
                {"id":2,"version":1,"createdBy":{"id":"","displayName":""},"isDeleted":false,"format":"html","text":""},
                {"id":3,"createdBy":{"displayName":"\u001b[31m\twriter"},"text":"null\n\u001b[0m","format":"future"}]}
                """,
        };
        using var http = new HttpClient(handler);
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, TwigJsonContext.Default.DiscussionRequest));
        var output = new StringWriter();
        var stderr = new StringWriter();
        var command = new BenchDiscussionCommand(runtime.GetRequiredService<CommandContext>() with { Stderr = stderr },
            () => Module(runtime, http), output, input);
        var before = Snapshot(runtime);
        (await command.ExecuteAsync(true, format)).ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(4);
        output.ToString().ShouldNotContain("\u001b");
        if (format == "minimal")
        {
            lines[0].Split('\t').ShouldBe(new[] { "\"1\"", "null", "null", "null", "null", "null", "true", "null", "null" });
            lines[1].Split('\t').ShouldBe(new[] { "\"2\"", "\"1\"", "\"\"", "\"\"", "null", "null", "false", "\"html\"", "\"\"" });
            var last = lines[2].Split('\t');
            last.Length.ShouldBe(9);
            last[6].ShouldBe("null");
            last[7].ShouldBe("\"unknown\"");
            using var author = JsonDocument.Parse(last[3]);
            author.RootElement.GetString().ShouldBe("\u001b[31m\twriter");
            using var body = JsonDocument.Parse(last[8]);
            body.RootElement.GetString().ShouldBe("null\n\u001b[0m");
        }
        else
        {
            lines[0].ShouldContain("authorId=null author=null createdAt=null modifiedAt=null deleted=true format=null body=null");
            lines[1].ShouldContain("authorId=\"\" author=\"\" createdAt=null modifiedAt=null deleted=false format=\"html\" body=\"\"");
            lines[2].ShouldContain("deleted=null format=\"unknown\" body=\"null\\n\\u001B[0m\"");
            lines[2].ShouldContain("author=\"\\u001B[31m\\twriter\"");
        }
        Snapshot(runtime).ShouldBe(before);
    }

    private async Task<(ServiceProvider Runtime, DiscussionRequest Request)> PrepareAsync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var cold = _fixture.Transport.SnapshotHttp();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Cached, never replaced by discussion"));
        var warm = _fixture.Transport.SnapshotHttp();
        warm.ShouldBe(cold with { Total = cold.Total + 2, Principal = cold.Principal + 2 },
            "First cached-item projection owns PAT attestation and the resource-scope GET.");
        _ = await runtime.GetRequiredService<IAuthenticationProvider>().GetAccessTokenAsync();
        _fixture.Transport.SnapshotHttp().ShouldBe(warm, "The shared native authentication provider is already warm.");
        await repository.SaveAsync(NativeBrowserFixture.Item(99, "Unrelated local intent"));
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(99, "note", null, null, "Retain unrelated note");
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(99);
        await runtime.GetRequiredService<IContextStore>().SetValueAsync("discussion-parity-sentinel", "unchanged");
        await runtime.GetRequiredService<IFieldDefinitionStore>().SaveBatchAsync([new("Custom.Proof", "Cached definition", "string", false)]);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = (await benches.CreateAsync("Discussion target"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        var other = (await benches.CreateAsync("Current stays here"))!;
        await benches.SetCurrentAsync(other.Id);
        var origin = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        return (runtime, new(1, new(origin.WorktreeRoot, origin.Binding.ConnectionRef, origin.Binding.BindingId, 42),
            bench.Name, bench.Id.ToString(CultureInfo.InvariantCulture), origin.Identity.IdentityId, null));
    }

    private static BrowserDiscussionModule Module(ServiceProvider runtime, HttpClient http) => new(
        runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<BenchEvaluator>(),
        runtime.GetRequiredService<IWorkItemRepository>(),
        new AdoRestClient(http, runtime.GetRequiredService<IAuthenticationProvider>(),
            "https://dev.azure.com/fixture", NativeBrowserFixture.Project, new WorkItemMapper()));

    private static void Refused(DiscussionResult result)
    {
        result.Status.ShouldBe("refused");
        result.Context.ShouldBeNull();
        result.Paging.ShouldBeNull();
        result.Comments.ShouldBeEmpty();
        result.Error.ShouldNotBeNull();
    }

    private string Snapshot(ServiceProvider runtime)
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
                    for (var index = 0; index < rows.FieldCount; index++)
                    {
                        var cell = rows.GetValue(index);
                        var text = cell is DBNull ? "<null>" : cell is byte[] bytes ? Convert.ToHexString(bytes)
                            : Convert.ToString(cell, CultureInfo.InvariantCulture)!;
                        row.Append(text.Length).Append(':').Append(text);
                    }
                    values.Add(row.ToString());
                }
                foreach (var value in values.Order(StringComparer.Ordinal)) result.AppendLine(value);
            }
        }

        foreach (var root in new[] { _fixture.Home, _fixture.Paths.TwigDir })
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                result.Append(file).Append(':').AppendLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        return result.ToString();
    }

    private sealed class DiscussionHttp : HttpMessageHandler
    {
        internal List<Uri> Uris { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal Func<Task>? BeforeReply { get; init; }
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal string? Body { get; init; }
        internal Exception? BodyReadFailure { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            Methods.Add(request.Method);
            if (BeforeReply is not null) await BeforeReply();
            return new(Status)
            {
                Content = BodyReadFailure is { } failure ? new StreamContent(new FailingBodyStream(failure))
                    : new StringContent(Body ?? (Uris.Count == 1
                    ? """{"count":1,"totalCount":1,"comments":[{"id":45,"version":1,"text":"Untrusted"}],"continuationToken":"opaque+next&value"}"""
                    : """{"count":0,"totalCount":1,"comments":[]}"""), Encoding.UTF8, "application/json"),
            };
        }

        private sealed class FailingBodyStream(Exception failure) : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
                => throw failure;
        }
    }
}

public sealed class BrowserDiscussionRequestTests
{
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("nested-unknown")]
    [InlineData("null-key")]
    [InlineData("missing-cursor")]
    [InlineData("malformed")]
    [InlineData("trailing")]
    [InlineData("depth")]
    public async Task StrictRequestRejectsAmbiguousOrIncompleteJson(string fault)
    {
        const string valid = """{"version":1,"itemKey":{"checkoutRoot":"root","connectionId":"connection","bindingId":"binding","workItemId":42},"bench":"B","expectBench":"7","expectIdentity":"I","cursor":null}""";
        var json = fault switch
        {
            "duplicate" => valid.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            "unknown" => valid.Replace("\"version\":1", "\"extra\":1,\"version\":1", StringComparison.Ordinal),
            "nested-unknown" => valid.Replace("\"workItemId\":42", "\"workItemId\":42,\"extra\":1", StringComparison.Ordinal),
            "null-key" => valid.Replace("""{"checkoutRoot":"root","connectionId":"connection","bindingId":"binding","workItemId":42}""", "null", StringComparison.Ordinal),
            "missing-cursor" => valid.Replace(",\"cursor\":null", "", StringComparison.Ordinal),
            "trailing" => valid + "{}",
            "depth" => new string('[', 33) + "0" + new string(']', 33),
            _ => "{",
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (fault == "null-key")
            BrowserDiscussionModule.Valid(await BenchDiscussionCommand.ReadRequestAsync(input, default)).ShouldBeFalse();
        else
            await Should.ThrowAsync<JsonException>(() => BenchDiscussionCommand.ReadRequestAsync(input, default));
    }

    [Fact]
    public async Task RequestByteLimitAndCancellationAreEnforcedBeforeAnyReader()
    {
        using var input = new MemoryStream(new byte[BenchDiscussionCommand.MaximumInputBytes + 1]);
        (await Should.ThrowAsync<ArgumentException>(() => BenchDiscussionCommand.ReadRequestAsync(input, default)))
            .Message.ShouldBe("input-too-large");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        input.Position = 0;
        await Should.ThrowAsync<OperationCanceledException>(() => BenchDiscussionCommand.ReadRequestAsync(input, canceled.Token));
    }

    [Fact]
    public async Task ExactRequestByteLimitIsAcceptedWithoutWeakeningTheEofRequirement()
    {
        const string json = """{"version":1,"itemKey":{"checkoutRoot":"root","connectionId":"connection","bindingId":"binding","workItemId":42},"bench":"B","expectBench":"7","expectIdentity":"I","cursor":null}""";
        var bytes = Encoding.UTF8.GetBytes(json + new string(' ', BenchDiscussionCommand.MaximumInputBytes - Encoding.UTF8.GetByteCount(json)));
        bytes.Length.ShouldBe(BenchDiscussionCommand.MaximumInputBytes);
        using var input = new MemoryStream(bytes);
        BrowserDiscussionModule.Valid(await BenchDiscussionCommand.ReadRequestAsync(input, default)).ShouldBeTrue();
        input.Position.ShouldBe(input.Length);
    }
}
