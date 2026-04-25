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

    // ──────────────────────── GetContextDbPath ────────────────────────

    [Fact]
    public void GetContextDbPath_CombinesOrgProjectIntoNestedPath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var result = TwigPaths.GetContextDbPath(twigDir, "dangreen-msft", "Twig");

        var expected = Path.Combine(twigDir, "dangreen-msft", "Twig", "twig.db");
        result.ShouldBe(expected);
    }

    [Fact]
    public void GetContextDbPath_SanitizesOrgAndProject()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var result = TwigPaths.GetContextDbPath(twigDir, "org/with/slashes", "project:bad");

        var expected = Path.Combine(twigDir, "org_with_slashes", "project_bad", "twig.db");
        result.ShouldBe(expected);
    }

    // ──────────────────────── ForContext ────────────────────────

    [Fact]
    public void ForContext_SetsAllPaths()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var paths = TwigPaths.ForContext(twigDir, "myorg", "myproj");

        paths.TwigDir.ShouldBe(twigDir);
        paths.ConfigPath.ShouldBe(Path.Combine(twigDir, "config"));
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "myorg", "myproj", "twig.db"));
    }

    [Fact]
    public void ForContext_SanitizesOrgAndProject()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var paths = TwigPaths.ForContext(twigDir, "org<bad>", "proj|bad");

        paths.DbPath.ShouldBe(Path.Combine(twigDir, "org_bad_", "proj_bad", "twig.db"));
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
    public void BuildPaths_WithOrgAndProject_ReturnsContextScopedPath()
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproj" };

        var paths = TwigPaths.BuildPaths(twigDir, config);

        paths.TwigDir.ShouldBe(twigDir);
        paths.ConfigPath.ShouldBe(Path.Combine(twigDir, "config"));
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "myorg", "myproj", "twig.db"));
    }

    [Theory]
    [InlineData("", "myproj")]
    [InlineData("myorg", "")]
    [InlineData("", "")]
    [InlineData(null, "myproj")]
    [InlineData("myorg", null)]
    [InlineData(null, null)]
    [InlineData("  ", "myproj")]
    [InlineData("myorg", "  ")]
    public void BuildPaths_WithEmptyOrgOrProject_ReturnsFlatPath(string? org, string? project)
    {
        var twigDir = Path.Combine("C:", "repo", ".twig");
        var config = new TwigConfiguration
        {
            Organization = org ?? string.Empty,
            Project = project ?? string.Empty,
        };

        var paths = TwigPaths.BuildPaths(twigDir, config);

        paths.TwigDir.ShouldBe(twigDir);
        paths.ConfigPath.ShouldBe(Path.Combine(twigDir, "config"));
        paths.DbPath.ShouldBe(Path.Combine(twigDir, "twig.db"));
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
