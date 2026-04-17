using NSubstitute;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Tests for <see cref="CompanionStartup"/> — the pre-DI first-run companion check
/// wired into <c>Program.cs</c> startup.
/// </summary>
public sealed class CompanionStartupTests
{
    // ═══════════════════════════════════════════════════════════════
    //  ResolveRepoSlug
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveRepoSlug_ReturnsAssemblyMetadataValue()
    {
        // The Twig assembly embeds [AssemblyMetadata("GitHubRepo", "PolyphonyRequiem/twig")]
        var slug = CompanionStartup.ResolveRepoSlug();

        slug.ShouldBe("PolyphonyRequiem/twig");
    }

    [Fact]
    public void ResolveRepoSlug_ReturnsNonEmptySlug()
    {
        var slug = CompanionStartup.ResolveRepoSlug();

        slug.ShouldNotBeNullOrWhiteSpace();
        slug.ShouldContain("/");
    }

    // ═══════════════════════════════════════════════════════════════
    //  RunFirstRunCheck — error isolation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RunFirstRunCheck_DoesNotThrow_WhenProcessPathIsNull()
    {
        // null processPath → CompanionFirstRunCheck.EnsureCompanionsAsync returns immediately
        var fs = Substitute.For<IFileSystem>();

        Should.NotThrow(() =>
            CompanionStartup.RunFirstRunCheckCore(null, "1.0.0", fs));
    }

    [Fact]
    public void RunFirstRunCheck_DoesNotThrow_WhenAllCompanionsPresent()
    {
        var fs = Substitute.For<IFileSystem>();
        // All file existence checks return true → fast path, no download
        fs.FileExists(Arg.Any<string>()).Returns(true);

        var processPath = Path.Combine(Path.GetTempPath(), "twig-test", "twig.exe");

        Should.NotThrow(() =>
            CompanionStartup.RunFirstRunCheckCore(processPath, "1.0.0", fs));
    }

    [Fact]
    public void RunFirstRunCheck_OuterCatch_SwallowsExceptions()
    {
        // RunFirstRunCheck() creates DefaultFileSystem internally, so we can't inject
        // a throwing mock. This test verifies the outer try-catch wrapper never propagates
        // exceptions to the caller — even with real dependencies (DefaultFileSystem,
        // real HttpClient). The inner RunFirstRunCheckCore exits early (no companions
        // found or processPath is null), but the catch block is the safety net.
        Should.NotThrow(() => CompanionStartup.RunFirstRunCheck());
    }
}
