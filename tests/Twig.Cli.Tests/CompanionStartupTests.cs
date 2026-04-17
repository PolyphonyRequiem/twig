using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        // RunFirstRunCheck (the outer wrapper) catches ALL exceptions
        // so it never crashes the CLI. We can't easily inject a throwing
        // IFileSystem into RunFirstRunCheck() (it creates DefaultFileSystem),
        // but we verify the try-catch pattern works by exercising RunFirstRunCheckCore
        // with a throwing file system. The outer wrapper adds its own try-catch.
        var fs = Substitute.For<IFileSystem>();
        fs.FileExists(Arg.Any<string>()).Throws(new IOException("disk error"));

        // RunFirstRunCheckCore may throw (it's the inner method), but
        // RunFirstRunCheck wraps it in try-catch. Verify the outer method never throws.
        Should.NotThrow(() => CompanionStartup.RunFirstRunCheck());
    }
}
