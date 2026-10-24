using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
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
    // CommandRegistrationModule wires the command. An explicitly configured provider without an
    // adapter must fail closed with its reason code; it must never fall back to ADO's report.
    [Theory]
    [InlineData("github", "provider-unconfigured:")]
    [InlineData("jira", "provider-unknown:")]
    public async Task ConfiguredProviderWithoutAdapter_FailsClosed_WithReasonCode(string stored, string reason)
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

    private static ServiceProvider BuildWithProvider(string? stored)
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration { Organization = "testorg", Project = "testproj", Provider = stored };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());
        return services.BuildServiceProvider();
    }
}