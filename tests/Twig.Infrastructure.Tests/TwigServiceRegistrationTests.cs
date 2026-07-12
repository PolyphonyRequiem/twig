using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests;

/// <summary>
/// Tests for TwigServiceRegistration.AddTwigCoreServices — specifically
/// the optional twigDir parameter added in T-2.1.
/// </summary>
public sealed class TwigServiceRegistrationTests
{
    [Fact]
    public void AddTwigCoreServices_RepoManifestOnly_CreatesMissingContextDatabase()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        var twigDir = Path.Combine(repoRoot, ".twig");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, WorkspaceDiscovery.RepoManifestFileName), "{}");

        try
        {
            var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
            var paths = TwigPaths.ForContext(twigDir, config.Organization, config.Project, repoRoot);
            var services = new ServiceCollection();
            services.AddTwigCoreServices(preloadedConfig: config, twigDir: twigDir, startDir: repoRoot);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<SqliteCacheStore>();

            File.Exists(paths.DbPath).ShouldBeTrue();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void AddTwigCoreServices_ConfigOnlyWorkspace_CreatesMissingContextDatabase()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        var twigDir = Path.Combine(repoRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "config"), "{}");

        try
        {
            var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
            var paths = TwigPaths.ForContext(twigDir, config.Organization, config.Project, repoRoot);
            var services = new ServiceCollection();
            services.AddTwigCoreServices(preloadedConfig: config, twigDir: twigDir, startDir: repoRoot);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<SqliteCacheStore>();

            File.Exists(paths.DbPath).ShouldBeTrue();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    [Fact]
    public void AddTwigCoreServices_WithTwigDir_UsesTwigDirForPaths()
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };
        var customDir = Path.Combine(Path.GetTempPath(), "custom-twig-dir");

        services.AddTwigCoreServices(preloadedConfig: config, twigDir: customDir);

        var provider = services.BuildServiceProvider();
        var paths = provider.GetRequiredService<TwigPaths>();

        paths.TwigDir.ShouldBe(customDir);
        paths.DbPath.ShouldBe(Path.Combine(customDir, "myorg", "myproj", "twig.db"));
        paths.ConfigPath.ShouldBe(Path.Combine(customDir, "config"));
    }

    [Fact]
    public void AddTwigCoreServices_WithNullTwigDir_FallsBackToCwd()
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };

        services.AddTwigCoreServices(preloadedConfig: config, twigDir: null);

        var provider = services.BuildServiceProvider();
        var paths = provider.GetRequiredService<TwigPaths>();

        var expectedDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        paths.TwigDir.ShouldBe(expectedDir);
    }

    [Fact]
    public void AddTwigCoreServices_NullConfig_WithTwigDir_ConfigFallbackUsesProvidedDir()
    {
        // When preloadedConfig is null, the config factory loads from twigDir/config.
        // TwigConfiguration.Load returns a default config if the file doesn't exist.
        var tempDir = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var services = new ServiceCollection();
            services.AddTwigCoreServices(preloadedConfig: null, twigDir: tempDir);

            var provider = services.BuildServiceProvider();

            // Config should load from tempDir/config (doesn't exist → empty default)
            var config = provider.GetRequiredService<TwigConfiguration>();
            config.ShouldNotBeNull();
            config.Organization.ShouldBe(string.Empty);

            // TwigPaths should use the provided twigDir
            var paths = provider.GetRequiredService<TwigPaths>();
            paths.TwigDir.ShouldBe(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

}
