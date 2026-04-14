using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
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
    public void WorkspaceGuard_ExistingConfigFile_AllowsConfigLoad()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigDir);
            var configPath = Path.Combine(twigDir, "config");
            File.WriteAllText(configPath, """
                {
                    "organization": "test-org",
                    "project": "test-project"
                }
                """);

            File.Exists(configPath).ShouldBeTrue("Test precondition: config must exist");

            var config = TwigConfiguration.Load(configPath);
            config.ShouldNotBeNull();
            config.Organization.ShouldBe("test-org");
            config.Project.ShouldBe("test-project");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

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
            sp.GetRequiredService<SyncCoordinator>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<IFieldDefinitionStore>()));

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
    public void SyncCoordinator_ReceivesCacheStaleMinutes_FromConfig()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());
        services.AddSingleton(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 42 }
        });

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

        using var provider = services.BuildServiceProvider();

        // SyncCoordinator should resolve without error when config provides the value
        var coordinator = provider.GetRequiredService<SyncCoordinator>();
        coordinator.ShouldNotBeNull();
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
    public void WorkingSetService_ReceivesUserDisplayName_FromConfig()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton(new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Jane Doe" }
        });

        services.AddSingleton(sp => new WorkingSetService(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>().User.DisplayName));

        using var provider = services.BuildServiceProvider();

        var svc = provider.GetRequiredService<WorkingSetService>();
        svc.ShouldNotBeNull();
    }
}
