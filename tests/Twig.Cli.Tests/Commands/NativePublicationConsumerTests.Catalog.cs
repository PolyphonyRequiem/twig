using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public void ClosedCafDeclarationsDoNotAdvertiseRetiredCommands()
    {
        using var catalog = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(catalog);
            Twig.TwigConsoleApp.Run(["--help"], _ => { }).ShouldBe(0);
            System.Text.RegularExpressions.Regex.IsMatch(catalog.ToString(),
                @"^  (new|tree-set|history|tui|mcp|draft|policy|connection (migrate|pin|default|reconcile-write)|bench browser)(?:[,\s]|$)",
                System.Text.RegularExpressions.RegexOptions.Multiline).ShouldBeFalse("inspect raw CAF declarations, not a filtered presentation");
            catalog.ToString().ShouldContain("auth login");
            catalog.ToString().ShouldContain("connection status");
            catalog.ToString().ShouldContain("proposal apply");
        }
        finally { Console.SetOut(previous); }
    }

    [Theory]
    [InlineData("show", "42")]
    [InlineData("query", "--title", "never read")]
    [InlineData("pending")]
    [InlineData("seed", "view")]
    [InlineData("proposal", "status", "--file", "never-open.json")]
    public void NoMarkerCoreDataReadsRefuseWithoutLegacyGraphOrInputIo(params string[] route)
    {
        var root = Path.Combine(_temp, "uninitialized");
        Directory.CreateDirectory(root);
        var args = route.Concat(["-o", "json"]).ToArray();
        using var error = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(error);
            Twig.TwigConsoleApp.Run(args, services =>
            {
                services.AddSingleton<IConnectionBindingService>(_bindings);
                services.AddTwigWorkspaceServices(root, args, Path.Combine(Home, "display.json"));
            }).ShouldBe(1);
            using var json = JsonDocument.Parse(error.ToString());
            json.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("Core workspace");
            Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
            _transport.TotalRequests.ShouldBe(0);
        }
        finally { Console.SetError(previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedProductionCatalogHasOnlyCoreDataAndNativeHostOwners(bool marker)
    {
        TwigPaths paths;
        if (marker) paths = (await RelationshipWorkspaceAsync()).Paths;
        else paths = (await AttachAsync()).GetRequiredService<TwigPaths>();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBe(marker);
        var requests = _transport.TotalRequests;
        var registration = new ServiceCollection();
        registration.AddSingleton<IConnectionBindingService>(_bindings);
        registration.AddTwigWorkspaceServices(paths.StartDir, ["connection", "status", "-o", "json"],
            Path.Combine(Home, "display.json"));
        var forbidden = new[]
        {
            "IAdoWorkItemService", "IWorkItemRepository", "IPendingChangeStore", "PendingChangeFlusher",
            "IPendingChangeFlusher", "SeedPublishOrchestrator", "PlanOperationExecutor", "IPlanLifecycleService",
            "IIdentityChangeEligibilityService", "IAsyncRenderer", "RenderingPipelineFactory",
            "SqliteCacheStore", "BrowserApplication", "BrowserRuntimeFactory", "BrowserHostCommand",
            "ShowCommand", "QueryCommand", "PlanCommand", "NewCommand", "HistoryCommand",
        };
        registration.Where(value => forbidden.Contains(value.ServiceType.Name, StringComparer.Ordinal)).ShouldBeEmpty();
        var runtime = registration.BuildServiceProvider();
        _runtimes.Add(runtime);
        runtime.GetRequiredService<AuthLoginCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<AuthPatCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<AuthIdentitiesCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<AuthGitHubCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<ConnectionBindCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<ConnectionListCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<ConnectionStatusCommand>().ShouldNotBeNull();
        runtime.GetRequiredService<ConnectionWritesCommand>().ShouldNotBeNull();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            (await runtime.GetRequiredService<ConnectionStatusCommand>().ExecuteAsync("json"))
                .ShouldBe(0, error.ToString());
            output.ToString().ShouldContain("not freshly authenticated");
            output.GetStringBuilder().Clear();
            (await runtime.GetRequiredService<ConnectionWritesCommand>().ExecuteAsync(false, output: "json"))
                .ShouldBe(0, error.ToString());
            output.ToString().ShouldBe("[]" + Environment.NewLine);
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
        if (marker) runtime.GetRequiredService<CoreCommandAdapter>().ShouldNotBeNull();
        else registration.Any(value => value.ServiceType == typeof(CoreCommandAdapter)).ShouldBeFalse();
        _transport.TotalRequests.ShouldBe(requests, "composition/resolution cannot acquire credentials or work metadata");
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("tree-set")]
    [InlineData("history")]
    [InlineData("tui")]
    [InlineData("mcp")]
    [InlineData("bench browser")]
    [InlineData("draft save")]
    [InlineData("connection reconcile-write")]
    [InlineData("connection migrate")]
    [InlineData("proposal latest")]
    [InlineData("policy assess")]
    [InlineData("workspace exclude")]
    public void ClosedCatalogRefusesRetiredCafRoutesBeforeComposition(string route)
    {
        var configured = 0;
        using var error = new StringWriter();
        var oldError = Console.Error;
        try
        {
            Console.SetError(error);
            var args = route.Split(' ').Concat(["-o", "json"]).ToArray();
            Twig.TwigConsoleApp.Run(args, _ => configured++).ShouldBe(1);
            configured.ShouldBe(0);
            using var json = JsonDocument.Parse(error.ToString());
            json.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("retired");
            _transport.TotalRequests.ShouldBe(0);
        }
        finally { Console.SetError(oldError); }
    }

    [Fact]
    public async Task RetiredRootLeavesNeverResolveLegacyCommandsOrLaunchers()
    {
        var infrastructure = typeof(CoreWorkspaceFactory).Assembly;
        foreach (var name in new[]
        {
            "Twig.Infrastructure.TwigServiceRegistration",
            "Twig.Infrastructure.DependencyInjection.NetworkServiceModule",
            "Twig.Infrastructure.Auth.ConnectionMigrationService",
            "Twig.Infrastructure.Auth.ConnectionBindingTransitionService",
            "Twig.Infrastructure.Auth.ConnectionDefaultTransitionService",
            "Twig.Infrastructure.Auth.IdentityChangeEligibilityService",
            "Twig.Infrastructure.Services.Mutation.AdoMutationProvider",
            "Twig.Infrastructure.Services.Claims.LocalClaimService",
        })
            infrastructure.GetType(name).ShouldBeNull("retired business executors must not remain compiled");
        typeof(IConnectionRemoteWriteReconciliationService).GetMethods()
            .Select(method => method.Name).ShouldBe(["InspectAsync"]);
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(Arg.Any<Type>()).Returns(_ => throw new InvalidOperationException("Unexpected command resolution"));
        var root = new TwigCommands(provider);
        using var error = new StringWriter();
        var oldError = Console.Error;
        try
        {
            Console.SetError(error);
            (await root.New(output: "json")).ShouldBe(1);
            (await root.TreeSet(output: "json")).ShouldBe(1);
            (await root.History(42, output: "json")).ShouldBe(1);
            (await root.BenchBrowser(output: "json")).ShouldBe(1);
            (await root.Tui(output: "json")).ShouldBe(1);
            (await root.Mcp()).ShouldBe(1);
            (await root.PlanLatest(output: "json")).ShouldBe(1);
            (await root.ConnectionReconcileWrite(output: "json")).ShouldBe(1);
            provider.DidNotReceive().GetService(Arg.Any<Type>());
        }
        finally { Console.SetError(oldError); }
    }

    [Fact]
    public async Task NativeHostUtilitiesUseRealNamedCredentialAndBindingWithoutLegacyWorkspace()
    {
        var root = Path.Combine(_temp, "host-only");
        Directory.CreateDirectory(root);
        File.Exists(Path.Combine(root, ".twig", CoreWorkspaceFactory.FileName)).ShouldBeFalse();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(ActorPat + "\n");
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldIn = Console.In;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(stdin);
            int Run(params string[] args) => Twig.TwigConsoleApp.Run(args, services =>
            {
                services.AddSingleton<IConnectionBindingService>(_bindings);
                services.AddTwigWorkspaceServices(root, args, Path.Combine(Home, "display.json"));
            });
            Run("auth", "pat", "--identity", "host", "--org", "contoso", "--stdin", "-o", "json").ShouldBe(0, stderr.ToString());
            _transport.WorkRequests.ShouldBe(0, "PAT enrollment attests the native principal but does not read work");
            var requests = _transport.TotalRequests;
            Run("auth", "identities", "-o", "json").ShouldBe(0, stderr.ToString());
            Run("connection", "bind", "--org", "contoso", "--project", "project", "--identity", "host", "--default", "-o", "json")
                .ShouldBe(0, stderr.ToString());
            Run("connection", "list", "--org", "contoso", "--project", "project", "-o", "json")
                .ShouldBe(0, stderr.ToString());
            _transport.TotalRequests.ShouldBe(requests);
            _transport.CommittedWrites.ShouldBe(0);
            Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
            stdout.ToString().ShouldNotContain(ActorPat);
            stderr.ToString().ShouldNotContain(ActorPat);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); Console.SetIn(oldIn); }
    }
}
