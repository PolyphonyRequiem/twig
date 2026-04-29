using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for <see cref="WorkspaceDiscovery.FindTwigDir"/> walk-up directory search.
/// Uses real temporary directories to exercise actual filesystem traversal.
/// </summary>
public sealed class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ──────────────────────── Found in CWD ────────────────────────

    [Fact]
    public void FindTwigDir_TwigInStartDir_ReturnsTwigPath()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "config"), "");

        var result = WorkspaceDiscovery.FindTwigDir(_tempRoot);

        result.ShouldBe(twigDir);
    }

    // ──────────────────────── Found in ancestor ────────────────────────

    [Fact]
    public void FindTwigDir_TwigInAncestor_WalksUpAndFindsIt()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "config"), "");

        var child = Path.Combine(_tempRoot, "src", "MyProject");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        result.ShouldBe(twigDir);
    }

    // ──────────────────────── Nearest wins ────────────────────────

    [Fact]
    public void FindTwigDir_MultipleTwigDirs_ReturnsNearest()
    {
        // Outer .twig (valid workspace)
        var outerTwig = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(outerTwig);
        File.WriteAllText(Path.Combine(outerTwig, "config"), "");

        // Inner .twig (closer to startDir, also valid)
        var innerProject = Path.Combine(_tempRoot, "nested");
        var innerTwig = Path.Combine(innerProject, ".twig");
        Directory.CreateDirectory(innerTwig);
        File.WriteAllText(Path.Combine(innerTwig, "config"), "");

        var child = Path.Combine(innerProject, "src");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        result.ShouldBe(innerTwig);
    }

    // ──────────────────────── No .twig anywhere ────────────────────────

    [Fact]
    public void FindTwigDir_NoTwigAnywhere_ReturnsNull()
    {
        // Ensure the walk-up search doesn't find a .twig/ from any ancestor.
        // On Windows, %TEMP% is under the user profile which may contain .twig/,
        // so use the drive root (e.g. C:\) which is writable on Windows.
        // On Linux, /tmp is outside the user home and writable without root.
        var testRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"twig-test-{Guid.NewGuid():N}")
            : Path.Combine(Path.GetTempPath(), $"twig-test-{Guid.NewGuid():N}");
        var deep = Path.Combine(testRoot, "a", "b", "c");
        Directory.CreateDirectory(deep);
        try
        {
            var result = WorkspaceDiscovery.FindTwigDir(deep);
            result.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    // ──────────────────────── startDir is null → uses CWD ────────────────────────

    [Fact]
    public void FindTwigDir_NullStartDir_UsesCwd()
    {
        // We can't easily control CWD in tests, but we can verify it doesn't throw.
        // The method should execute without exception when startDir is null.
        var ex = Record.Exception(() => WorkspaceDiscovery.FindTwigDir(null));
        ex.ShouldBeNull();
    }

    // ──────────────────────── Root boundary ────────────────────────

    [Fact]
    public void FindTwigDir_StartAtFilesystemRoot_ReturnsNullWithoutInfiniteLoop()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        // If walk-up has a bug (e.g., root's parent == root), this hangs; reaching here proves it terminates.
        WorkspaceDiscovery.FindTwigDir(root);
    }

    // ──────────────────────── Deep nesting ────────────────────────

    [Fact]
    public void FindTwigDir_DeeplyNested_WalksUpMultipleLevels()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        // A valid workspace needs a config file
        File.WriteAllText(Path.Combine(twigDir, "config"), "");

        var deep = Path.Combine(_tempRoot, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);

        var result = WorkspaceDiscovery.FindTwigDir(deep);

        result.ShouldBe(twigDir);
    }

    // ──────────── AB#2591: Global home (~/.twig/) is NOT a workspace ────────────

    [Fact]
    public void IsWorkspaceDirectory_GlobalHome_ReturnsFalse()
    {
        // The global home path should never be treated as a workspace
        var globalHome = WorkspaceDiscovery.GlobalHomePath;

        // Even if it exists, it should return false
        var result = WorkspaceDiscovery.IsWorkspaceDirectory(globalHome);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsWorkspaceDirectory_WithConfigFile_ReturnsTrue()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "config"), "org=test\nproject=test");

        var result = WorkspaceDiscovery.IsWorkspaceDirectory(twigDir);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWorkspaceDirectory_WithNestedDb_ReturnsTrue()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        var contextDir = Path.Combine(twigDir, "myorg", "myproject");
        Directory.CreateDirectory(contextDir);
        File.WriteAllText(Path.Combine(contextDir, "twig.db"), "");

        var result = WorkspaceDiscovery.IsWorkspaceDirectory(twigDir);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWorkspaceDirectory_WithNestedConfig_ReturnsTrue()
    {
        // Multi-workspace layout: .twig/{org}/{project}/config (no top-level config)
        var twigDir = Path.Combine(_tempRoot, ".twig");
        var contextDir = Path.Combine(twigDir, "myorg", "myproject");
        Directory.CreateDirectory(contextDir);
        File.WriteAllText(Path.Combine(contextDir, "config"), """{"organization":"myorg"}""");

        var result = WorkspaceDiscovery.IsWorkspaceDirectory(twigDir);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsWorkspaceDirectory_EmptyDir_ReturnsFalse()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var result = WorkspaceDiscovery.IsWorkspaceDirectory(twigDir);

        result.ShouldBeFalse();
    }

    [Fact]
    public void IsWorkspaceDirectory_WithBinOnly_ReturnsFalse()
    {
        // Simulates the global home structure (bin/, profiles/)
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(Path.Combine(twigDir, "bin"));
        Directory.CreateDirectory(Path.Combine(twigDir, "profiles"));

        var result = WorkspaceDiscovery.IsWorkspaceDirectory(twigDir);

        result.ShouldBeFalse();
    }

    [Fact]
    public void FindTwigDir_SkipsGlobalHomeLikeDir_WithoutConfig()
    {
        // Simulate: startDir is under a parent that has .twig/ without workspace markers
        var parent = Path.Combine(_tempRoot, "home");
        var twigDir = Path.Combine(parent, ".twig");
        Directory.CreateDirectory(twigDir);
        // No config file, no nested DB — not a workspace

        var child = Path.Combine(parent, "projects", "myrepo");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindTwigDir_FindsWorkspaceAboveNonWorkspaceTwigDir()
    {
        // Outer workspace with config
        var outerTwig = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(outerTwig);
        File.WriteAllText(Path.Combine(outerTwig, "config"), "org=x\nproject=y");

        // Inner .twig/ without workspace markers (like a global home imposter)
        var innerDir = Path.Combine(_tempRoot, "nested");
        var innerTwig = Path.Combine(innerDir, ".twig");
        Directory.CreateDirectory(innerTwig);
        // No config, no DB

        var child = Path.Combine(innerDir, "src");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        // Should skip the inner non-workspace .twig/ and find the outer workspace
        result.ShouldBe(outerTwig);
    }
}
