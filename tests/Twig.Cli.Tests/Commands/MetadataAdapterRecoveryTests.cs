using System.Net;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class MetadataAdapterRecoveryTests
{
    [Theory]
    [InlineData("new")]
    [InlineData("process")]
    public async Task SuccessfulRealMetadataSupportsRecoveryAndStructuredUnknown(string command)
    {
        using var handler = new MetadataHandler("no-failure", 0);
        using var http = new HttpClient(handler);
        var service = new AdoIterationService(http, new Auth(), "https://dev.azure.com/example", "Example");
        var (exit, stdout, stderr, writes) = await RunCommand(command, service);
        writes.ShouldBe(0);
        if (command == "new")
        {
            exit.ShouldBe(1);
            stdout.ShouldBeEmpty();
            using var error = JsonDocument.Parse(stderr);
            error.RootElement.GetProperty("error").GetString()!.ShouldContain("Unknown field reference name(s): Custom.Unknown");
        }
        else
        {
            exit.ShouldBe(0);
            stderr.ShouldBeEmpty();
            using var output = JsonDocument.Parse(stdout);
            output.RootElement.GetProperty("types").EnumerateArray().ShouldContain(row => row.GetProperty("typeName").GetString() == "Task");
        }
        handler.Requests.ShouldContain(r => r.Path.EndsWith("/fields"));
    }

    [Theory]
    [InlineData("new", "fields", 401)]
    [InlineData("new", "fields", 403)]
    [InlineData("new", "fields", 0)]
    [InlineData("process", "fields", 401)]
    [InlineData("process", "fields", 403)]
    [InlineData("process", "fields", 0)]
    [InlineData("process", "processconfiguration", 401)]
    [InlineData("process", "processconfiguration", 403)]
    [InlineData("process", "processconfiguration", 0)]
    public async Task RealAdapterFailureProducesOneStructuredErrorAndNoWrites(string command, string route, int status)
    {
        using var handler = new MetadataHandler(route, status);
        using var http = new HttpClient(handler);
        var service = new AdoIterationService(http, new Auth(), "https://dev.azure.com/example", "Example");
        var (exit, stdout, stderr, writes) = await RunCommand(command, service);
        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr);
        var message = error.RootElement.GetProperty("error").GetString()!;
        message.ShouldContain("Metadata refresh failed");
        message.ShouldNotContain("Metadata not ready");
        message.ShouldContain(status == 0 ? "offline" : status == 403 ? "403" : "uthentication");
        writes.ShouldBe(0);
        handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Get && !r.Path.Contains("/workitems"));
        handler.Failures.ShouldBe(status == 0 ? 1 : 2);
        if (route == "processconfiguration") handler.Requests.ShouldContain(r => r.Path.EndsWith("/workitemtypes"));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("process")]
    public async Task RealAdapterCancellationDoesNotBecomeMetadataFailure(string command)
    {
        using var cts = new CancellationTokenSource();
        using var handler = new MetadataHandler(command == "new" ? "fields" : "processconfiguration", 0, cts);
        using var http = new HttpClient(handler);
        var service = new AdoIterationService(http, new Auth(), "https://dev.azure.com/example", "Example");
        await Should.ThrowAsync<OperationCanceledException>(() => RunCommand(command, service, cts.Token));
    }

    [Theory]
    [InlineData("fields", 401)]
    [InlineData("fields", 403)]
    [InlineData("fields", 0)]
    [InlineData("processconfiguration", 401)]
    [InlineData("processconfiguration", 403)]
    [InlineData("processconfiguration", 0)]
    public async Task ExistingTolerantReadDoesNotPoisonLaterStrictRecovery(string route, int status)
    {
        using var handler = new MetadataHandler(route, status);
        using var http = new HttpClient(handler);
        var service = new AdoIterationService(http, new Auth(), "https://dev.azure.com/example", "Example");
        var original = Console.Error;
        using var warning = new StringWriter();
        Console.SetError(warning);
        try
        {
            if (route == "fields") (await service.GetFieldDefinitionsAsync()).ShouldBeEmpty();
            else (await service.GetProcessConfigurationAsync()).TaskBacklog.ShouldBeNull();
        }
        finally { Console.SetError(original); }
        warning.ToString().ShouldContain("Could not fetch");
        var (exit, stdout, stderr, writes) = await RunCommand(route == "fields" ? "new" : "process", service);
        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr);
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("Metadata refresh failed");
        writes.ShouldBe(0);
    }

    private static async Task<(int Exit, string Stdout, string Stderr, int Writes)> RunCommand(string command, IIterationService service, CancellationToken ct = default)
    {
        var fields = Substitute.For<IFieldDefinitionStore>();
        IReadOnlyList<FieldDefinition> rows = [];
        fields.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => rows);
        fields.SaveBatchAsync(Arg.Any<IReadOnlyList<FieldDefinition>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            rows = call.Arg<IReadOnlyList<FieldDefinition>>();
            return Task.CompletedTask;
        });
        var types = Substitute.For<IProcessTypeStore>();
        var savedTypes = new List<ProcessTypeRecord>();
        types.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => savedTypes.ToArray());
        types.SaveAsync(Arg.Any<ProcessTypeRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            savedTypes.Add(call.Arg<ProcessTypeRecord>());
            return Task.CompletedTask;
        });
        var ado = Substitute.For<IAdoWorkItemService>();
        var items = Substitute.For<IWorkItemRepository>();
        var formatter = new OutputFormatterFactory(new HumanOutputFormatter());
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exit;
            if (command == "new")
            {
                var cmd = new NewCommand(ado, items, Substitute.For<IContextStore>(), fields,
                    Substitute.For<IEditorLauncher>(), formatter, new HintEngine(new DisplayConfig { Hints = false }),
                    new TwigConfiguration(), new SeedFactory(), Substitute.For<IStagedIdentityRegistry>(),
                    ReferenceProfileBuilder.UnpinnedSprintPolicy(), iterationService: service);
                exit = await cmd.ExecuteAsync("Fixture", "Task", fields: ["Custom.Unknown=x"], outputFormat: "json", ct: ct);
            }
            else
            {
                var cmd = new ProcessCommand(null, types, fields, formatter, new RendererFactory(), stderr: stderr, iterationService: service);
                exit = await cmd.ExecuteAsync(outputFormat: "json", refresh: true, ct: ct);
            }
            return (exit, stdout.ToString(), stderr.ToString(), ado.ReceivedCalls().Count() + items.ReceivedCalls().Count());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousErr); }
    }

    private sealed class Auth : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("fixture-token");
        public void InvalidateToken() { }
    }

    private sealed class MetadataHandler(string failureRoute, int status, CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public int Failures { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            if (path.EndsWith('/' + failureRoute, StringComparison.Ordinal))
            {
                Failures++;
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
                }
                if (status == 0) throw new HttpRequestException("fixture transport failure");
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"message\":\"fixture denied\"}") });
            }
            var body = path.EndsWith("/workitemtypes", StringComparison.Ordinal)
                ? "{\"value\":[{\"name\":\"Task\",\"states\":[{\"name\":\"Open\",\"category\":\"Proposed\"}]}]}"
                : path.EndsWith("/fields", StringComparison.Ordinal)
                    ? "{\"value\":[{\"referenceName\":\"System.Title\",\"name\":\"Title\",\"type\":\"string\"}]}"
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
