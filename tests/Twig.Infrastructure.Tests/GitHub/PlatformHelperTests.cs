using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="PlatformHelper"/> — shared RID detection and asset lookup
/// extracted from <c>SelfUpdateCommand</c>.
/// </summary>
public sealed class PlatformHelperTests
{
    // ═══════════════════════════════════════════════════════════════
    //  DetectRid
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DetectRid_ReturnsKnownPrefix()
    {
        var rid = PlatformHelper.DetectRid();
        rid.ShouldNotBeNull();

        var isKnown = rid.StartsWith("win-", StringComparison.Ordinal)
            || rid.StartsWith("linux-", StringComparison.Ordinal)
            || rid.StartsWith("osx-", StringComparison.Ordinal);

        isKnown.ShouldBeTrue($"RID '{rid}' does not start with a known OS prefix");
    }

    [Fact]
    public void DetectRid_ContainsArchitecture()
    {
        var rid = PlatformHelper.DetectRid();
        rid.ShouldNotBeNull();

        // RID format: {os}-{arch}
        var parts = rid.Split('-');
        parts.Length.ShouldBeGreaterThanOrEqualTo(2);

        var knownArchitectures = new[] { "x64", "arm64", "x86" };
        knownArchitectures.ShouldContain(parts[1]);
    }

    // ═══════════════════════════════════════════════════════════════
    //  FindAsset
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FindAsset_WindowsRid_LooksForZip()
    {
        var release = MakeRelease("twig-win-x64.zip");

        var (asset, archiveName) = PlatformHelper.FindAsset(release, "win-x64");

        archiveName.ShouldBe("twig-win-x64.zip");
        asset.ShouldNotBeNull();
        asset.Name.ShouldBe("twig-win-x64.zip");
    }

    [Fact]
    public void FindAsset_LinuxRid_LooksForTarGz()
    {
        var release = MakeRelease("twig-linux-x64.tar.gz");

        var (asset, archiveName) = PlatformHelper.FindAsset(release, "linux-x64");

        archiveName.ShouldBe("twig-linux-x64.tar.gz");
        asset.ShouldNotBeNull();
        asset.Name.ShouldBe("twig-linux-x64.tar.gz");
    }

    [Fact]
    public void FindAsset_OsxRid_LooksForTarGz()
    {
        var release = MakeRelease("twig-osx-arm64.tar.gz");

        var (asset, archiveName) = PlatformHelper.FindAsset(release, "osx-arm64");

        archiveName.ShouldBe("twig-osx-arm64.tar.gz");
        asset.ShouldNotBeNull();
    }

    [Fact]
    public void FindAsset_NoMatchingAsset_ReturnsNullAsset()
    {
        var release = MakeRelease("twig-win-x64.zip");

        var (asset, archiveName) = PlatformHelper.FindAsset(release, "linux-x64");

        asset.ShouldBeNull();
        archiveName.ShouldBe("twig-linux-x64.tar.gz");
    }

    [Fact]
    public void FindAsset_CaseInsensitiveMatch()
    {
        var release = MakeRelease("Twig-Win-X64.ZIP");

        var (asset, _) = PlatformHelper.FindAsset(release, "win-x64");

        // Asset name matching is case-insensitive per the implementation
        asset.ShouldNotBeNull();
    }

    [Fact]
    public void FindAsset_EmptyAssets_ReturnsNull()
    {
        var release = new GitHubReleaseInfo(
            Tag: "v1.0.0",
            Name: "v1.0.0",
            Body: "",
            PublishedAt: null,
            Assets: []);

        var (asset, archiveName) = PlatformHelper.FindAsset(release, "win-x64");

        asset.ShouldBeNull();
        archiveName.ShouldBe("twig-win-x64.zip");
    }

    [Fact]
    public void FindAsset_MultipleAssets_FindsCorrectOne()
    {
        var assets = new List<GitHubReleaseAssetInfo>
        {
            new("twig-win-x64.zip", "https://example.com/win", 1024),
            new("twig-linux-x64.tar.gz", "https://example.com/linux", 2048),
            new("twig-osx-arm64.tar.gz", "https://example.com/osx", 3072),
        };
        var release = new GitHubReleaseInfo("v1.0.0", "v1.0.0", "", null, assets);

        var (asset, _) = PlatformHelper.FindAsset(release, "linux-x64");

        asset.ShouldNotBeNull();
        asset.BrowserDownloadUrl.ShouldBe("https://example.com/linux");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static GitHubReleaseInfo MakeRelease(string assetName)
    {
        var assets = new List<GitHubReleaseAssetInfo>
        {
            new(assetName, $"https://example.com/{assetName}", 1024)
        };
        return new GitHubReleaseInfo(
            Tag: "v1.0.0",
            Name: "v1.0.0",
            Body: "",
            PublishedAt: null,
            Assets: assets);
    }
}
