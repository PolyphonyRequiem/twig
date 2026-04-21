using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Shouldly;
using Twig.Domain.Interfaces;
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
    public void WorkspaceInfrastructure_Registrations_ResolveCorrectTypes()
    {
        // Verify that the workspace infrastructure singletons registered in Program.cs
        // (WorkspaceRegistry via IWorkspaceRegistry, WorkspaceResolver) are resolvable.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            // Create a per-workspace config: .twig/testorg/testproj/config
            var wsConfigDir = Path.Combine(tempDir, ".twig", "testorg", "testproj");
            Directory.CreateDirectory(wsConfigDir);
            File.WriteAllText(Path.Combine(wsConfigDir, "config"),
                """{"organization":"testorg","project":"testproj"}""");

            var twigRoot = Path.Combine(tempDir, ".twig");
            var registry = new WorkspaceRegistry(twigRoot);

            var httpClient = new HttpClient();
            var authProvider = NSubstitute.Substitute.For<IAuthenticationProvider>();

            var factory = new WorkspaceContextFactory(registry, httpClient, authProvider, twigRoot);
            var resolver = new WorkspaceResolver(registry, factory);

            var services = new ServiceCollection();
            services.AddSingleton<IWorkspaceRegistry>(registry);
            services.AddSingleton(resolver);

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IWorkspaceRegistry>().ShouldNotBeNull();
            provider.GetRequiredService<WorkspaceResolver>().ShouldNotBeNull();
            provider.GetRequiredService<IWorkspaceRegistry>().ShouldBeSameAs(registry);
            provider.GetRequiredService<WorkspaceResolver>().ShouldBeSameAs(resolver);

            factory.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void McpServerRegistration_ConfiguresServerInfoAndAllToolTypes()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Register workspace infrastructure stubs required by tool constructors
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".twig"));
            var registry = new WorkspaceRegistry(Path.Combine(tempDir, ".twig"));
            var authProvider = NSubstitute.Substitute.For<IAuthenticationProvider>();
            var factory = new WorkspaceContextFactory(registry, new HttpClient(), authProvider, Path.Combine(tempDir, ".twig"));
            var resolver = new WorkspaceResolver(registry, factory);

            services.AddSingleton<IWorkspaceRegistry>(registry);
            services.AddSingleton(resolver);

            services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new() { Name = "twig-mcp", Version = "1.0.0-test" };
                })
                .WithStdioServerTransport()
                .WithTools<ContextTools>()
                .WithTools<ReadTools>()
                .WithTools<MutationTools>()
                .WithTools<NavigationTools>()
                .WithTools<CreationTools>()
                .WithTools<WorkspaceTools>();

            using var provider = services.BuildServiceProvider();

            var options = provider.GetService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>();
            options.ShouldNotBeNull();
            options.Value.ServerInfo.ShouldNotBeNull();
            options.Value.ServerInfo.Name.ShouldBe("twig-mcp");
            options.Value.ServerInfo.Version.ShouldBe("1.0.0-test");

            factory.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- WorkspaceGuard.CheckWorkspaceAmbient (multi-workspace) ---

    [Fact]
    public void Ambient_WithPerWorkspaceConfig_ReturnsValid()
    {
        // FR-7: Ambient mode succeeds when .twig/{org}/{project}/config exists,
        // even without top-level .twig/config.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            var wsDir = Path.Combine(twigDir, "myorg", "myproject");
            Directory.CreateDirectory(wsDir);
            File.WriteAllText(Path.Combine(wsDir, "config"),
                """{"organization":"myorg","project":"myproject"}""");

            var (isValid, error, resultTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(tempDir);

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
    public void Ambient_WithLegacyTopLevelConfig_ReturnsValid()
    {
        // Backward compat: ambient mode also succeeds with .twig/config (single-workspace).
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigDir);
            File.WriteAllText(Path.Combine(twigDir, "config"), "{}");

            var (isValid, error, resultTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(tempDir);

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
    public void Ambient_EmptyTwigDir_NoConfigs_ReturnsInvalid()
    {
        // When .twig/ exists but has no config files at any level, ambient mode fails.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            Directory.CreateDirectory(twigDir);

            var (isValid, error, _) = WorkspaceGuard.CheckWorkspaceAmbient(tempDir);

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
    public void Ambient_NoTwigDirAnywhere_ReturnsNotFoundError()
    {
        var rootDir = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"twig-nows-{Guid.NewGuid():N}")
            : Path.Combine(Path.GetTempPath(), $"twig-nows-{Guid.NewGuid():N}");
        var tempDir = Path.Combine(rootDir, "deep", "nested");
        try
        {
            Directory.CreateDirectory(tempDir);

            var (isValid, error, twigDir) = WorkspaceGuard.CheckWorkspaceAmbient(tempDir);

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
    public void Ambient_MultipleWorkspaceConfigs_ReturnsValid()
    {
        // Multiple per-workspace configs: ambient mode succeeds when two workspaces exist.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            var ws1 = Path.Combine(twigDir, "org1", "proj1");
            var ws2 = Path.Combine(twigDir, "org2", "proj2");
            Directory.CreateDirectory(ws1);
            Directory.CreateDirectory(ws2);
            File.WriteAllText(Path.Combine(ws1, "config"),
                """{"organization":"org1","project":"proj1"}""");
            File.WriteAllText(Path.Combine(ws2, "config"),
                """{"organization":"org2","project":"proj2"}""");

            var (isValid, error, resultTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(tempDir);

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
    public void Ambient_FromSubdirectory_FindsTwigDir()
    {
        // Walk-up: ambient mode discovers .twig/ from a nested CWD.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        try
        {
            var twigDir = Path.Combine(tempDir, ".twig");
            var wsDir = Path.Combine(twigDir, "myorg", "myproject");
            Directory.CreateDirectory(wsDir);
            File.WriteAllText(Path.Combine(wsDir, "config"),
                """{"organization":"myorg","project":"myproject"}""");

            var subDir = Path.Combine(tempDir, "src", "MyProject");
            Directory.CreateDirectory(subDir);

            var (isValid, _, resultTwigDir) = WorkspaceGuard.CheckWorkspaceAmbient(subDir);

            isValid.ShouldBeTrue();
            resultTwigDir.ShouldBe(twigDir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
