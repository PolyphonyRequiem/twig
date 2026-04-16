using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests;

/// <summary>
/// Tests for Program.cs bootstrap logic: workspace guard, DI registrations, and MCP server setup.
/// These tests verify the DI composition patterns used in Program.cs without starting the host.
/// </summary>
public sealed class ProgramBootstrapTests
{
    [Fact]
    public void DomainServices_FactoryRegistrations_ResolveCorrectTypes()
    {
        // Verify that the factory-based DI registrations (DD-10) register all
        // 6 domain orchestration services with the correct concrete types.
        var services = new ServiceCollection();

        // Register mock infrastructure dependencies
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var iterationService = Substitute.For<IIterationService>();
        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();

        services.AddSingleton(contextStore);
        services.AddSingleton(workItemRepo);
        services.AddSingleton(adoService);
        services.AddSingleton(pendingChangeStore);
        services.AddSingleton(iterationService);
        services.AddSingleton(processTypeStore);
        services.AddSingleton(fieldDefStore);
        services.AddSingleton(linkRepo);
        services.AddSingleton(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" }
        });

        // Register domain services exactly as Program.cs does (factory lambdas)
        services.AddSingleton(sp => new ActiveItemResolver(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>()));

        services.AddSingleton(sp => new ProtectedCacheWriter(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        services.AddSingleton(sp => new SyncCoordinator(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IWorkItemLinkRepository>(),
            sp.GetRequiredService<TwigConfiguration>().Display.CacheStaleMinutes));

        services.AddSingleton(sp => new WorkingSetService(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

        services.AddSingleton(sp => new RefreshOrchestrator(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<ProtectedCacheWriter>(),
            sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<SyncCoordinator>()));

        services.AddSingleton(sp => new StatusOrchestrator(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<ActiveItemResolver>(),
            sp.GetRequiredService<WorkingSetService>(),
            sp.GetRequiredService<SyncCoordinator>()));

        services.AddSingleton(sp => new McpPendingChangeFlusher(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>()));

        using var provider = services.BuildServiceProvider();

        // All 6 domain services + McpPendingChangeFlusher must resolve
        provider.GetRequiredService<ActiveItemResolver>().ShouldNotBeNull();
        provider.GetRequiredService<ProtectedCacheWriter>().ShouldNotBeNull();
        provider.GetRequiredService<SyncCoordinator>().ShouldNotBeNull();
        provider.GetRequiredService<WorkingSetService>().ShouldNotBeNull();
        provider.GetRequiredService<RefreshOrchestrator>().ShouldNotBeNull();
        provider.GetRequiredService<StatusOrchestrator>().ShouldNotBeNull();
        provider.GetRequiredService<McpPendingChangeFlusher>().ShouldNotBeNull();
    }

    [Fact]
    public void McpServerRegistration_ConfiguresServerInfo()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "twig-mcp", Version = "1.0.0-test" };
            })
            .WithStdioServerTransport()
            .WithTools<ContextTools>()
            .WithTools<ReadTools>()
            .WithTools<MutationTools>();

        using var provider = services.BuildServiceProvider();

        // The MCP server options should be resolvable after registration
        var options = provider.GetService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>();
        options.ShouldNotBeNull();
        options.Value.ServerInfo.ShouldNotBeNull();
        options.Value.ServerInfo.Name.ShouldBe("twig-mcp");
        options.Value.ServerInfo.Version.ShouldBe("1.0.0-test");
    }

    [Fact]
    public void WorkspaceGuard_MissingConfigFile_ReturnsInvalidWithMessage()
    {
        // FR-11: When .twig/config is missing, the guard should return IsValid=false
        // with a clear error message telling the user to run 'twig init'.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            // Create .twig/ dir without config so walk-up stops here
            Directory.CreateDirectory(Path.Combine(tempDir, ".twig"));

            var (isValid, error, _) = WorkspaceGuard.CheckWorkspace(tempDir);

            isValid.ShouldBeFalse();
            error.ShouldNotBeNull();
            error.ShouldContain("twig init");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceGuard_ConfigFileExistsAtExactPath_ReturnsValid()
    {
        // FR-11: The guard checks for a .twig/config *file* — not just a .twig/ directory.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigDir);
            File.WriteAllText(Path.Combine(twigDir, "config"), "{}");

            var (isValid, error, resultTwigDir) = WorkspaceGuard.CheckWorkspace(tempDir);

            isValid.ShouldBeTrue();
            error.ShouldBeNull();
            resultTwigDir.ShouldBe(twigDir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceGuard_NoTwigDirAnywhere_ReturnsNotFoundError()
    {
        // Walk-up search: when no .twig/ exists anywhere up the ancestor chain,
        // return a clear error directing the user to run 'twig init'.
        // Use a drive-root-level directory to avoid inheriting a real .twig/
        // from an ancestor (e.g., the user's home directory).
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var rootDir = Path.Combine(driveRoot, $"twig-nows-{Guid.NewGuid():N}");
        var tempDir = Path.Combine(rootDir, "deep", "nested");
        try
        {
            Directory.CreateDirectory(tempDir);

            var (isValid, error, twigDir) = WorkspaceGuard.CheckWorkspace(tempDir);

            isValid.ShouldBeFalse();
            error.ShouldBe("No twig workspace found. Run 'twig init' in your project root.");
            twigDir.ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public void DiscoveredTwigDir_FromSubdirectory_ThreadedCorrectly()
    {
        // Walk-up integration: when started from a subdirectory, the guard discovers
        // .twig/ in a parent, and that path is threaded to AddTwigCoreServices.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigDir);
            File.WriteAllText(Path.Combine(twigDir, "config"), "{}");

            // Create a nested subdirectory to simulate running from src/MyProject/
            var subDir = Path.Combine(tempDir, "src", "MyProject");
            Directory.CreateDirectory(subDir);

            var (isValid, _, discoveredTwigDir) = WorkspaceGuard.CheckWorkspace(subDir);
            isValid.ShouldBeTrue();
            discoveredTwigDir.ShouldBe(twigDir);

            var configPath = Path.Combine(discoveredTwigDir!, "config");
            var config = TwigConfiguration.Load(configPath);

            var services = new ServiceCollection();
            services.AddTwigCoreServices(config, discoveredTwigDir);

            using var provider = services.BuildServiceProvider();
            var paths = provider.GetRequiredService<TwigPaths>();

            // TwigPaths should point at the parent's .twig/, not the subdirectory
            paths.TwigDir.ShouldBe(twigDir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
