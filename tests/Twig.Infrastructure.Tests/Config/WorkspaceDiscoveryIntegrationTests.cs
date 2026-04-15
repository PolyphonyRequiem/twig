using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Integration tests verifying that <see cref="WorkspaceDiscovery.FindTwigDir"/>
/// results flow correctly through DI registration — the pattern used by Program.cs.
/// </summary>
public sealed class WorkspaceDiscoveryIntegrationTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceDiscoveryIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"twig-walkup-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void FindTwigDir_FromSubdirectory_ResolvesCorrectPathsThroughDI()
    {
        // Arrange: .twig/ at root, start from a nested subdirectory
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        var subdirectory = Path.Combine(_tempRoot, "src", "MyProject");
        Directory.CreateDirectory(subdirectory);

        // Act: simulate Program.cs pattern — FindTwigDir() from subdirectory, pass to DI
        var discoveredDir = WorkspaceDiscovery.FindTwigDir(subdirectory);
        var resolvedDir = discoveredDir ?? Path.Combine(subdirectory, ".twig");

        var config = new TwigConfiguration { Organization = "testorg", Project = "testproj" };
        var services = new ServiceCollection();
        services.AddTwigCoreServices(preloadedConfig: config, twigDir: resolvedDir);
        var provider = services.BuildServiceProvider();
        var paths = provider.GetRequiredService<TwigPaths>();

        // Assert: paths should point to the root .twig/, not the subdirectory
        paths.TwigDir.ShouldBe(twigDir);
        paths.ConfigPath.ShouldBe(Path.Combine(twigDir, "config"));
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "testorg", "testproj", "twig.db"));
    }

    [Fact]
    public void FindTwigDir_NoWorkspace_FallsBackToCwdRelativePath()
    {
        // Use a drive-root-level directory to avoid inheriting a real .twig/
        // from an ancestor (e.g., the user's home directory).
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var isolated = Path.Combine(driveRoot, $"twig-nows-{Guid.NewGuid():N}");
        var subdirectory = Path.Combine(isolated, "empty", "project");
        Directory.CreateDirectory(subdirectory);

        try
        {
            // Act: simulate Program.cs fallback pattern
            var discoveredDir = WorkspaceDiscovery.FindTwigDir(subdirectory);
            var resolvedDir = discoveredDir ?? Path.Combine(subdirectory, ".twig");

            // Assert: FindTwigDir returns null, fallback is CWD-relative
            discoveredDir.ShouldBeNull();
            resolvedDir.ShouldBe(Path.Combine(subdirectory, ".twig"));
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

    [Fact]
    public void FindTwigDir_SmartLanding_DetectsWorkspaceFromSubdirectory()
    {
        // Arrange: .twig/ at root, start from a nested subdirectory
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        var subdirectory = Path.Combine(_tempRoot, "src", "MyProject", "Controllers");
        Directory.CreateDirectory(subdirectory);

        // Act: simulate Program.cs smart landing — FindTwigDir() from subdirectory
        var twigDirCheck = WorkspaceDiscovery.FindTwigDir(subdirectory);

        // Assert: workspace found → FindTwigDir returns the root .twig/
        twigDirCheck.ShouldNotBeNull();
        twigDirCheck.ShouldBe(twigDir);
    }

    [Fact]
    public void FindTwigDir_SmartLanding_NoWorkspace_ReturnsNull()
    {
        // Use drive-root isolation to avoid inheriting a real .twig/
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var isolated = Path.Combine(driveRoot, $"twig-nows-{Guid.NewGuid():N}");
        var subdirectory = Path.Combine(isolated, "empty", "project");
        Directory.CreateDirectory(subdirectory);

        try
        {
            // Act: simulate Program.cs smart landing no-workspace branch
            var twigDirCheck = WorkspaceDiscovery.FindTwigDir(subdirectory);

            // Assert: no workspace → FindTwigDir returns null
            twigDirCheck.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

}
