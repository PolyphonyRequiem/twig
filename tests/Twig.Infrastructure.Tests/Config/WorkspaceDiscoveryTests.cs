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

        var result = WorkspaceDiscovery.FindTwigDir(_tempRoot);

        result.ShouldBe(twigDir);
    }

    // ──────────────────────── Found in ancestor ────────────────────────

    [Fact]
    public void FindTwigDir_TwigInAncestor_WalksUpAndFindsIt()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var child = Path.Combine(_tempRoot, "src", "MyProject");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        result.ShouldBe(twigDir);
    }

    // ──────────────────────── Nearest wins ────────────────────────

    [Fact]
    public void FindTwigDir_MultipleTwigDirs_ReturnsNearest()
    {
        // Outer .twig
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".twig"));

        // Inner .twig (closer to startDir)
        var innerProject = Path.Combine(_tempRoot, "nested");
        var innerTwig = Path.Combine(innerProject, ".twig");
        Directory.CreateDirectory(innerTwig);

        var child = Path.Combine(innerProject, "src");
        Directory.CreateDirectory(child);

        var result = WorkspaceDiscovery.FindTwigDir(child);

        result.ShouldBe(innerTwig);
    }

    // ──────────────────────── No .twig anywhere ────────────────────────

    [Fact]
    public void FindTwigDir_NoTwigAnywhere_ReturnsNull()
    {
        // Use a directory near the filesystem root to avoid inheriting a real
        // .twig/ from an ancestor (e.g., the user's home directory).
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var testRoot = Path.Combine(driveRoot, $"twig-test-{Guid.NewGuid():N}");
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

    // ──────────────────────── Deep nesting ────────────────────────

    [Fact]
    public void FindTwigDir_DeeplyNested_WalksUpMultipleLevels()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var deep = Path.Combine(_tempRoot, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);

        var result = WorkspaceDiscovery.FindTwigDir(deep);

        result.ShouldBe(twigDir);
    }
}
