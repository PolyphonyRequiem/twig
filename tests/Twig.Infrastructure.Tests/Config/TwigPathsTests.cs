using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for TwigPaths: path sanitization, context-scoped DB path derivation,
/// legacy DB path, and ForContext factory method (ITEM-139).
/// </summary>
public class TwigPathsTests
{
    // ──────────────────────── SanitizePathSegment ────────────────────────

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("dangreen-msft", "dangreen-msft")]
    [InlineData("Twig", "Twig")]
    public void SanitizePathSegment_ValidNames_ReturnedUnchanged(string input, string expected)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("org/project", "org_project")]
    [InlineData("org\\project", "org_project")]
    [InlineData("org:project", "org_project")]
    [InlineData("org*project", "org_project")]
    [InlineData("org?project", "org_project")]
    [InlineData("org\"project", "org_project")]
    [InlineData("org<project", "org_project")]
    [InlineData("org>project", "org_project")]
    [InlineData("org|project", "org_project")]
    public void SanitizePathSegment_UnsafeChars_ReplacedWithUnderscore(string input, string expected)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a/b\\c:d*e", "a_b_c_d_e")]
    [InlineData("org<>|name", "org___name")]
    public void SanitizePathSegment_MultipleUnsafeChars_AllReplaced(string input, string expected)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("  leading", "leading")]
    [InlineData("trailing  ", "trailing")]
    [InlineData("  both  ", "both")]
    public void SanitizePathSegment_LeadingTrailingWhitespace_Trimmed(string input, string expected)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(".dotprefix", "dotprefix")]
    [InlineData("dotsuffix.", "dotsuffix")]
    [InlineData("..both..", "both")]
    public void SanitizePathSegment_LeadingTrailingDots_Trimmed(string input, string expected)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SanitizePathSegment_EmptyOrWhitespace_ReturnsUnderscore(string? input)
    {
        TwigPaths.SanitizePathSegment(input).ShouldBe("_");
    }

    [Fact]
    public void SanitizePathSegment_OnlyDots_ReturnsUnderscore()
    {
        TwigPaths.SanitizePathSegment("...").ShouldBe("_");
    }

    [Fact]
    public void SanitizePathSegment_OnlyUnsafeChars_PreservesAllReplacements()
    {
        // Each unsafe char is replaced by _ individually; no dots to trim
        TwigPaths.SanitizePathSegment("/:*").ShouldBe("___");
    }

    // ──────────────────────── GetContextDbPath (T1 §4.2.4) ────────────────────────

    [Fact]
    public void GetContextDbPath_ReturnsCanonicalCachePath_IgnoringOrgAndProject()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        // T1 §4.2.4 clean cutover: one DB per worktree at .twig/cache/twig.db.
        // org/project no longer segment the path.
        var result = TwigPaths.GetContextDbPath(twigDir, "dangreen-msft", "Twig");
        result.ShouldBe(Path.Combine(twigDir, "cache", "twig.db"));
    }

    [Fact]
    public void GetCacheDbPath_IsTheCanonicalCachePath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        TwigPaths.GetCacheDbPath(twigDir).ShouldBe(Path.Combine(twigDir, "cache", "twig.db"));
    }

    // ──────────────────────── ForContext ────────────────────────

    [Fact]
    public void ForContext_UsesCacheDbPath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var paths = TwigPaths.ForContext(twigDir, "myorg", "myproj");

        paths.TwigDir.ShouldBe(twigDir);
        paths.ConfigPath.ShouldBe(Path.Combine(twigDir, "config"));
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "cache", "twig.db"));
    }

    // ──────────────────────── GetLegacyDbPath ────────────────────────

    [Fact]
    public void GetLegacyDbPath_ReturnsFlatDbPath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        TwigPaths.GetLegacyDbPath(twigDir).ShouldBe(Path.Combine(twigDir, "twig.db"));
    }

    // ──────────────────────── BuildPaths ────────────────────────

    [Fact]
    public void BuildPaths_AlwaysReturnsCachePath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };

        var paths = TwigPaths.BuildPaths(twigDir, config);
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "cache", "twig.db"));
    }

    // ──────────────────────── TrackingFilePath ────────────────────────

    [Fact]
    public void TrackingFilePath_ReturnsCombinedPath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        paths.TrackingFilePath.ShouldBe(Path.Combine(twigDir, "tracking.json"));
    }

    [Fact]
    public void TrackingFilePath_ForContext_UsesRootTwigDir()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var paths = TwigPaths.ForContext(twigDir, "myorg", "myproj");

        // TrackingFilePath lives at TwigDir level, not inside the org/project subdirectory
        paths.TrackingFilePath.ShouldBe(Path.Combine(twigDir, "tracking.json"));
    }

    // ──────────────────────── Constructor ────────────────────────

    [Fact]
    public void Constructor_StoresAllPaths()
    {
        var paths = new TwigPaths("dir", "config", "db");
        paths.TwigDir.ShouldBe("dir");
        paths.ConfigPath.ShouldBe("config");
        paths.DbPath.ShouldBe("db");
    }
}
