using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#1170: <c>twig connection capabilities</c> reports exactly what the active provider declares,
/// joined with its probe evidence. Missing evidence is unknown/not-probed, never unsupported, and
/// denied availability never rewrites the declared implementation.
/// </summary>
public sealed class ConnectionCapabilitiesCommandTests
{
    private readonly ITelemetryClient _telemetry = Substitute.For<ITelemetryClient>();

    private ConnectionCapabilitiesCommand CreateCommand(Func<IWorkItemProvider?> resolver) =>
        new(resolver, new OutputFormatterFactory(new HumanOutputFormatter()), telemetryClient: _telemetry);

    private static IWorkItemProvider Provider(
        ProviderCapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityAvailabilityEvidence>? evidence = null)
    {
        var provider = Substitute.For<IWorkItemProvider>();
        provider.Capabilities.Returns(descriptor);
        provider.ProbeCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(evidence ?? (IReadOnlyList<CapabilityAvailabilityEvidence>)[]);
        return provider;
    }

    private static Dictionary<string, JsonElement> ByKind(string json, out string provider)
    {
        using var doc = JsonDocument.Parse(json);
        provider = doc.RootElement.GetProperty("provider").GetString()!;
        return doc.RootElement.GetProperty("relationships").EnumerateArray()
            .ToDictionary(e => e.GetProperty("kind").GetString()!, e => e.Clone());
    }

    [Fact]
    public async Task Json_GitHub_ReportsDeclaredImplementation_AndNotProbedAvailability()
    {
        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var provider);
        provider.ShouldBe("github");
        kinds.Keys.ShouldBe(["parent-child", "predecessor-successor", "related"], ignoreOrder: true);
        kinds["parent-child"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["predecessor-successor"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["related"].GetProperty("implementation").GetString().ShouldBe("unsupported");
        foreach (var entry in kinds.Values)
        {
            entry.GetProperty("storage").GetString().ShouldBe("none");
            entry.GetProperty("availability").GetString().ShouldBe("unknown");
            entry.GetProperty("reason").GetString().ShouldBe(CapabilityReport.NotProbedReason);
        }
    }

    [Fact]
    public async Task Json_DeniedEvidence_KeepsNativeImplementation()
    {
        var provider = Provider(ProviderCapabilityCatalog.Ado,
        [
            new CapabilityAvailabilityEvidence(LinkKind.ParentChild, CapabilityAvailability.Available, null),
            new CapabilityAvailabilityEvidence(LinkKind.Related, CapabilityAvailability.Unavailable, "denied"),
        ]);

        var (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => provider).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var providerKind);
        providerKind.ShouldBe("ado");
        kinds["parent-child"].GetProperty("availability").GetString().ShouldBe("available");
        kinds["parent-child"].GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        kinds["related"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["related"].GetProperty("availability").GetString().ShouldBe("unavailable");
        kinds["related"].GetProperty("reason").GetString().ShouldBe("denied");
        kinds["predecessor-successor"].GetProperty("availability").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task Json_EmulatedDeclaration_ReportsProviderChosenStorage()
    {
        var descriptor = new ProviderCapabilityDescriptor("test",
        [
            new(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.None),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Emulated, EmulationStorage.LocalOnly),
            new(LinkKind.Related, CapabilityImplementation.Unsupported, EmulationStorage.None),
        ]);

        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(descriptor)).ExecuteAsync("json"));

        var kinds = ByKind(output, out _);
        kinds["predecessor-successor"].GetProperty("implementation").GetString().ShouldBe("emulated");
        kinds["predecessor-successor"].GetProperty("storage").GetString().ShouldBe("local-only");
    }

    [Theory]
    [InlineData("available", null)]
    [InlineData("unavailable", "read-denied")]
    [InlineData("unknown", "read-incomplete")]
    public async Task Json_GitHub_ReportsSuppliedEvidence_WithoutChangingDeclarationOrOtherKinds(
        string token, string? reason)
    {
        CapabilityTokens.TryResolve(CapabilityTokens.Availability, token, out var availability).ShouldBeTrue();
        var provider = Provider(ProviderCapabilityCatalog.GitHub,
            [new(LinkKind.ParentChild, availability, reason)]);

        var (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => provider).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var providerKind);
        providerKind.ShouldBe("github");
        kinds["parent-child"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["parent-child"].GetProperty("storage").GetString().ShouldBe("none");
        kinds["parent-child"].GetProperty("availability").GetString().ShouldBe(token);
        if (reason is null)
            kinds["parent-child"].GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        else
            kinds["parent-child"].GetProperty("reason").GetString().ShouldBe(reason);
        foreach (var kind in new[] { "predecessor-successor", "related" })
        {
            kinds[kind].GetProperty("availability").GetString().ShouldBe("unknown");
            kinds[kind].GetProperty("reason").GetString().ShouldBe(CapabilityReport.NotProbedReason);
        }
        kinds["predecessor-successor"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["related"].GetProperty("implementation").GetString().ShouldBe("unsupported");
        await provider.Received(1).ProbeCapabilitiesAsync(Arg.Any<CancellationToken>());
    }

    // The report must follow the resolved descriptor's declarations, never a lookup keyed by the
    // provider name. Each well-known name carries the OTHER catalog's declarations, so any
    // name-keyed shortcut reports the wrong implementation for at least one kind.
    [Theory]
    [InlineData("github", "native")]
    [InlineData("ado", "unsupported")]
    public async Task Json_FollowsDeclarations_NotProviderName(string name, string related)
    {
        var declarations = name == "github" ? ProviderCapabilityCatalog.Ado : ProviderCapabilityCatalog.GitHub;
        var descriptor = new ProviderCapabilityDescriptor(name, declarations.Relationships);

        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(descriptor)).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var provider);
        provider.ShouldBe(name);
        kinds["related"].GetProperty("implementation").GetString().ShouldBe(related);
    }

    // The real (not yet DI-registered) GitHub adapter, passed straight to the command: its own
    // descriptor drives the report, its probe evidence comes only from its latest read, and
    // producing the report makes no HTTP request.
    [Fact]
    public async Task RealGitHubAdapter_ReportsOwnDescriptor_AndLatestReadEvidence_WithoutHttp()
    {
        using var transport = new CountingDeniedTransport();
        using var http = new HttpClient(transport);
        var adapter = new GitHubWorkItemProvider(http);

        var (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => adapter).ExecuteAsync("json"));

        exit.ShouldBe(0);
        transport.Requests.ShouldBe(0);
        var kinds = ByKind(output, out var provider);
        provider.ShouldBe("github");
        kinds.Values.ShouldAllBe(e => e.GetProperty("reason").GetString() == CapabilityReport.NotProbedReason);

        await Should.ThrowAsync<HttpRequestException>(() => adapter.FetchSnapshotAsync(
            new ExternalWorkItemIdentity("github", "https://api.github.com", "I_denied")));
        var requestsAfterRead = transport.Requests;
        requestsAfterRead.ShouldBeGreaterThan(0);

        (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => adapter).ExecuteAsync("json"));

        exit.ShouldBe(0);
        transport.Requests.ShouldBe(requestsAfterRead);
        kinds = ByKind(output, out _);
        foreach (var kind in new[] { "parent-child", "predecessor-successor" })
        {
            kinds[kind].GetProperty("implementation").GetString().ShouldBe("native");
            kinds[kind].GetProperty("availability").GetString().ShouldBe("unavailable");
            kinds[kind].GetProperty("reason").GetString().ShouldBe("read-denied");
        }

        kinds["related"].GetProperty("implementation").GetString().ShouldBe("unsupported");
        kinds["related"].GetProperty("reason").GetString().ShouldBe(CapabilityReport.NotProbedReason);
    }

    private sealed class CountingDeniedTransport : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        }
    }

    // The repository's established contract (RendererFactory) maps json, json-full and json-compact to
    // one indented JSON renderer; json-compact is not single-line anywhere. Pin that this command
    // agrees, so a compact alias cannot drift into a different shape than the other JSON aliases.
    [Theory]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    public async Task JsonAliases_ProduceIdenticalOutputToJson(string alias)
    {
        var (_, baseline) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync("json"));
        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync(alias));

        exit.ShouldBe(0);
        output.ShouldBe(baseline);
        ByKind(output, out _).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Minimal_IsPlainText_WithoutAnsiEscapes()
    {
        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync("minimal"));

        output.ShouldNotContain("\u001b");
    }

    [Fact]
    public async Task Minimal_EmitsOneTabSeparatedLinePerKind()
    {
        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync("minimal"));

        exit.ShouldBe(0);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.ShouldContain("related\tunsupported\tnone\tunknown\tnot-probed");
        lines.Count(l => l.Contains('\t')).ShouldBe(3);
    }

    [Fact]
    public async Task Json_GitHubSchema_ReportsFieldStateAndRuleSupportSeparately()
    {
        var provider = new SchemaMetadataProvider();

        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => provider).ExecuteAsync("json", includeGitHubSchema: true));

        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var schema = document.RootElement.GetProperty("schema");
        schema.GetProperty("availability").GetString().ShouldBe("available");
        schema.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "labels")
            .GetProperty("support").GetString().ShouldBe("supported");
        schema.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "assignees")
            .GetProperty("support").GetString().ShouldBe("supported");
        schema.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "milestone")
            .GetProperty("support").GetString().ShouldBe("supported");
        schema.GetProperty("states").GetProperty("values").GetArrayLength().ShouldBe(2);
        schema.GetProperty("rules").GetProperty("process").GetProperty("support").GetString()
            .ShouldBe("unsupported");
        schema.GetProperty("rules").GetProperty("fields").GetProperty("support").GetString()
            .ShouldBe("unsupported");
        schema.GetProperty("types").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Minimal_GitHubSchema_UsesFiveColumnRows()
    {
        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => new SchemaMetadataProvider())
                .ExecuteAsync("minimal", includeGitHubSchema: true));

        exit.ShouldBe(0);
        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', StringSplitOptions.None))
            .ToArray();
        rows.ShouldAllBe(row => row.Length == 5);
        rows.ShouldContain(row => row[0] == "field" && row[1] == "labels" && row[3] == "supported");
        rows.ShouldContain(row => row[0] == "rule" && row[1] == "process" && row[3] == "unsupported");
    }

    [Fact]
    public async Task GitHubSchemaOption_RefusesAdoWithoutCallingProvider()
    {
        var provider = Provider(ProviderCapabilityCatalog.Ado);

        var (exit, stderr) = await StderrCapture.RunAsync(
            () => CreateCommand(() => provider).ExecuteAsync("json", includeGitHubSchema: true));

        exit.ShouldBe(2);
        stderr.ShouldContain("requires the active GitHub provider");
        await provider.DidNotReceive().ProbeCapabilitiesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Human_NamesProviderAndEachKind()
    {
        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => Provider(ProviderCapabilityCatalog.GitHub)).ExecuteAsync("human"));

        exit.ShouldBe(0);
        output.ShouldContain("github");
        output.ShouldContain("parent-child");
        output.ShouldContain("unsupported");
        output.ShouldContain("not-probed");
    }

    [Fact]
    public async Task NoProvider_ExitsOne_WithoutReport()
    {
        var (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => null).ExecuteAsync("json"));

        exit.ShouldBe(1);
        output.ShouldNotContain("relationships");
    }

    [Fact]
    public async Task ProviderResolutionFailure_ExitsOne()
    {
        var (exit, _) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => throw new InvalidOperationException("no workspace")).ExecuteAsync("json"));

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task DuplicateEvidence_IsRefused()
    {
        var descriptor = ProviderCapabilityCatalog.GitHub;
        var provider = Provider(descriptor,
        [
            new CapabilityAvailabilityEvidence(LinkKind.ParentChild, CapabilityAvailability.Available, null),
            new CapabilityAvailabilityEvidence(LinkKind.ParentChild, CapabilityAvailability.Unavailable, "denied"),
        ]);

        var (exit, output) = await StdoutCapture.RunAsync(() => CreateCommand(() => provider).ExecuteAsync("json"));

        exit.ShouldBe(1);
        output.ShouldNotContain("relationships");
    }

    [Fact]
    public async Task ProbeFailure_ExitsOne()
    {
        var provider = Provider(ProviderCapabilityCatalog.Ado);
        provider.ProbeCapabilitiesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var (exit, _) = await StdoutCapture.RunAsync(() => CreateCommand(() => provider).ExecuteAsync("json"));

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Telemetry_RecordsOnlyCommandName()
    {
        await StdoutCapture.RunAsync(() => CreateCommand(() => Provider(ProviderCapabilityCatalog.Ado)).ExecuteAsync("json"));

        _telemetry.Received(1).TrackEvent(
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, string>>(p => p["command"] == "connection capabilities" && !p.ContainsKey("provider")),
            Arg.Any<Dictionary<string, double>>());
    }

    // Composed through #1167's real provider factory (NetworkServiceModule), resolved exactly as
    // CommandRegistrationModule wires the command. A provider that cannot be resolved (unknown, or
    // GitHub without explicit github.* scope) must fail closed with its reason code; it must never
    // fall back to ADO's report.
    [Theory]
    [InlineData("github", "provider-unconfigured:")]
    [InlineData("jira", "provider-unknown:")]
    public async Task UnresolvableProvider_FailsClosed_WithReasonCode(string stored, string reason)
    {
        using var services = BuildWithProvider(stored);

        var (exit, stderr) = await StderrCapture.RunAsync(
            () => CreateCommand(() => services.GetService<IWorkItemProvider>()).ExecuteAsync("json"));

        exit.ShouldBe(1);
        stderr.ShouldContain(reason);
        stderr.ShouldNotContain("relationships");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ado")]
    public async Task ConfiguredAdoProvider_ReportsAdoCatalog(string? stored)
    {
        using var services = BuildWithProvider(stored);

        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => services.GetService<IWorkItemProvider>()).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var provider);
        provider.ShouldBe("ado");
        kinds.Values.ShouldAllBe(e => e.GetProperty("implementation").GetString() == "native");
    }

    // #1167 activation: with explicit github.* scope the real factory resolves the registered GitHub
    // adapter, and the report is that adapter's own descriptor, labelled native/unsupported per kind.
    // Composing and reporting acquire no credential and make no GitHub request.
    [Fact]
    public async Task ConfiguredGitHubProvider_ReportsRegisteredAdapterDescriptor_WithoutCredentialOrHttp()
    {
        var tokens = new CountingTokenSource();
        using var transport = new CountingDeniedTransport();
        using var services = BuildWithProvider("github",
            new GitHubConfig { Host = "github.com", Repository = "octo/repo" }, tokens, transport);

        var active = services.GetRequiredService<IWorkItemProvider>();
        active.ShouldBeOfType<GitHubWorkItemProvider>();
        active.Capabilities.ShouldBeSameAs(ProviderCapabilityCatalog.GitHub);

        var (exit, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(() => services.GetService<IWorkItemProvider>()).ExecuteAsync("json"));

        exit.ShouldBe(0);
        var kinds = ByKind(output, out var provider);
        provider.ShouldBe("github");
        kinds["parent-child"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["predecessor-successor"].GetProperty("implementation").GetString().ShouldBe("native");
        kinds["related"].GetProperty("implementation").GetString().ShouldBe("unsupported");
        kinds.Values.ShouldAllBe(e => e.GetProperty("availability").GetString() == "unknown"
            && e.GetProperty("reason").GetString() == CapabilityReport.NotProbedReason);
        tokens.Calls.ShouldBe(0);
        transport.Requests.ShouldBe(0);
    }

    private sealed class CountingTokenSource : IGitHubTokenSource
    {
        public int Calls { get; private set; }

        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult("fixture-github-token");
        }
    }

    private sealed class SchemaMetadataProvider : IWorkItemProvider, IProviderSchemaMetadataReader
    {
        public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.GitHub;

        public Task<IReadOnlyList<CapabilityAvailabilityEvidence>> ProbeCapabilitiesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CapabilityAvailabilityEvidence>>([]);

        public Task<ProviderSchemaMetadata> DiscoverSchemaMetadataAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderSchemaMetadata(
                "github",
                ProviderSchemaDiscoveryAvailability.Available,
                null,
                [new ProviderSchemaTypeMetadata("Issue", ProviderSchemaElementSupport.Supported, null)],
                [
                    new("labels", "LabelConnection!", ProviderSchemaElementSupport.Supported, null),
                    new("assignees", "UserConnection!", ProviderSchemaElementSupport.Supported, null),
                    new("milestone", "Milestone", ProviderSchemaElementSupport.Supported, null),
                ],
                new ProviderSchemaStateMetadata(
                    "IssueState", ProviderSchemaElementSupport.Supported, ["CLOSED", "OPEN"], null),
                [
                    new(LinkKind.ParentChild, ProviderSchemaElementSupport.Supported, ["parent", "subIssues"], null),
                    new(LinkKind.PredecessorSuccessor, ProviderSchemaElementSupport.Supported, ["blockedBy", "blocking"], null),
                    new(LinkKind.Related, ProviderSchemaElementSupport.Unsupported, [], "relationship-not-supported-by-adapter"),
                ],
                new ProviderSchemaRuleSupport(
                    ProviderSchemaElementSupport.Unsupported, "provider-does-not-expose-process-rules",
                    ProviderSchemaElementSupport.Unsupported, "provider-does-not-expose-field-rules")));
    }

    private static ServiceProvider BuildWithProvider(
        string? stored,
        GitHubConfig? gitHub = null,
        IGitHubTokenSource? tokens = null,
        HttpMessageHandler? gitHubTransport = null)
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration { Organization = "testorg", Project = "testproj", Provider = stored, GitHub = gitHub };
        if (tokens is not null)
            services.AddSingleton(tokens);
        if (gitHubTransport is not null)
            services.AddSingleton(new GitHubTransportOverride(() => gitHubTransport));
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());
        return services.BuildServiceProvider();
    }
}