using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="SemVerComparer"/>, <see cref="SelfUpdateCommand"/> RID/asset detection,
/// and <see cref="SelfUpdateCommand.ExecuteAsync"/> command flow.
/// </summary>
public class SelfUpdateCommandTests
{
    // ── SemVerComparer: numeric comparison ─────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("1.2.0", "1.1.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("0.1.0", "0.2.0", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void SemVerComparer_NumericComparison(string a, string b, int expectedSign)
    {
        var result = SemVerComparer.Compare(a, b);
        Math.Sign(result).ShouldBe(expectedSign);
    }

    // ── SemVerComparer: pre-release handling ───────────────────────────

    [Fact]
    public void SemVerComparer_PreReleaseIsLessThanRelease()
    {
        // SemVer §11: 1.0.1-alpha.0.3 < 1.0.1
        var result = SemVerComparer.Compare("1.0.1-alpha.0.3", "1.0.1");
        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void SemVerComparer_ReleaseIsGreaterThanPreRelease()
    {
        var result = SemVerComparer.Compare("1.0.1", "1.0.1-alpha.0.3");
        result.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void SemVerComparer_BothPreRelease_AreEqual()
    {
        // Pre-release-to-pre-release ordering is not implemented — treated as equal
        var result = SemVerComparer.Compare("1.0.1-alpha.1", "1.0.1-beta.2");
        result.ShouldBe(0);
    }

    [Fact]
    public void SemVerComparer_BothRelease_AreEqual()
    {
        var result = SemVerComparer.Compare("1.0.0", "1.0.0");
        result.ShouldBe(0);
    }

    // ── SemVerComparer: v prefix stripping ─────────────────────────────

    [Theory]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "v1.0.0", 0)]
    [InlineData("v1.0.0", "v1.0.0", 0)]
    [InlineData("v1.0.0", "v2.0.0", -1)]
    [InlineData("V1.0.0", "1.0.0", 0)]
    public void SemVerComparer_VPrefixStripping(string a, string b, int expectedSign)
    {
        var result = SemVerComparer.Compare(a, b);
        Math.Sign(result).ShouldBe(expectedSign);
    }

    [Fact]
    public void SemVerComparer_VPrefixWithPreRelease()
    {
        var result = SemVerComparer.Compare("v1.0.1-alpha.0.3", "v1.0.1");
        result.ShouldBeLessThan(0);
    }

    // ── Upgrade decision logic ─────────────────────────────────────────

    [Fact]
    public void UpgradeDecision_NewerVersionAvailable_ComparisonIsNegative()
    {
        var result = SemVerComparer.Compare("0.1.0", "2.0.0");
        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void UpgradeDecision_CurrentLessThanLatest_ShouldUpdate()
    {
        var result = SemVerComparer.Compare("1.0.0", "1.1.0");
        result.ShouldBeLessThan(0);
    }

    [Fact]
    public void UpgradeDecision_CurrentEqualsLatest_AlreadyUpToDate()
    {
        var result = SemVerComparer.Compare("1.0.0", "1.0.0");
        result.ShouldBe(0);
    }

    [Fact]
    public void UpgradeDecision_CurrentGreaterThanLatest_AlreadyUpToDate()
    {
        var result = SemVerComparer.Compare("2.0.0", "1.0.0");
        result.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void UpgradeDecision_PreReleaseCurrent_ShouldUpdate()
    {
        var result = SemVerComparer.Compare("1.0.1-alpha.0.3", "1.0.1");
        result.ShouldBeLessThan(0);
    }

    // ── RID detection ──────────────────────────────────────────────────

    [Fact]
    public void DetectRid_ReturnsNonNullValue()
    {
        var rid = SelfUpdateCommand.DetectRid();
        rid.ShouldNotBeNull();
        rid.ShouldNotBeEmpty();
    }

    [Fact]
    public void DetectRid_ReturnsKnownFormat()
    {
        var rid = SelfUpdateCommand.DetectRid();
        rid.ShouldNotBeNull();

        rid.ShouldContain("-");
        var parts = rid.Split('-');
        parts.Length.ShouldBeGreaterThanOrEqualTo(2);

        var osPart = parts[0];
        osPart.ShouldBeOneOf("win", "linux", "osx");
    }

    // ── FindAsset: exercises production code directly ───────────────────

    [Theory]
    [InlineData("win-x64", "twig-win-x64.zip")]
    [InlineData("linux-x64", "twig-linux-x64.tar.gz")]
    [InlineData("osx-x64", "twig-osx-x64.tar.gz")]
    [InlineData("osx-arm64", "twig-osx-arm64.tar.gz")]
    public void FindAsset_KnownRids_ReturnsCorrectAsset(string rid, string expectedAssetName)
    {
        var release = new GitHubReleaseInfo(
            "v1.0.0", "Release 1.0.0", "",
            new[]
            {
                new GitHubReleaseAssetInfo("twig-win-x64.zip", "https://example.com/twig-win-x64.zip", 2048),
                new GitHubReleaseAssetInfo("twig-linux-x64.tar.gz", "https://example.com/twig-linux-x64.tar.gz", 3072),
                new GitHubReleaseAssetInfo("twig-osx-x64.tar.gz", "https://example.com/twig-osx-x64.tar.gz", 3072),
                new GitHubReleaseAssetInfo("twig-osx-arm64.tar.gz", "https://example.com/twig-osx-arm64.tar.gz", 3072),
            });

        var (asset, archiveName) = SelfUpdateCommand.FindAsset(release, rid);

        asset.ShouldNotBeNull();
        asset.Name.ShouldBe(expectedAssetName);
        archiveName.ShouldBe(expectedAssetName);
    }

    [Fact]
    public void FindAsset_NoMatchingAsset_ReturnsNull()
    {
        var release = new GitHubReleaseInfo(
            "v1.0.0", "Release 1.0.0", "",
            new[]
            {
                new GitHubReleaseAssetInfo("twig-win-x64.zip", "https://example.com/twig-win-x64.zip", 2048),
            });

        var (asset, archiveName) = SelfUpdateCommand.FindAsset(release, "linux-arm64");

        asset.ShouldBeNull();
        archiveName.ShouldBe("twig-linux-arm64.tar.gz");
    }

    [Fact]
    public void FindAsset_WindowsRid_UsesZipExtension()
    {
        var release = new GitHubReleaseInfo("v1.0.0", "R", "", []);
        var (_, archiveName) = SelfUpdateCommand.FindAsset(release, "win-x64");
        archiveName.ShouldEndWith(".zip");
    }

    [Fact]
    public void FindAsset_LinuxRid_UsesTarGzExtension()
    {
        var release = new GitHubReleaseInfo("v1.0.0", "R", "", []);
        var (_, archiveName) = SelfUpdateCommand.FindAsset(release, "linux-x64");
        archiveName.ShouldEndWith(".tar.gz");
    }

    // ── ExecuteAsync: command flow tests ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoReleasesFound_Returns0()
    {
        var stubService = new StubReleaseService(latestRelease: null);
        var stubUpdater = new SelfUpdater(new HttpClient());
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyUpToDate_Returns0()
    {
        // The assembly version will be something like "0.0.0" in test context.
        // We supply a release with the same version (or older) to trigger the "up to date" path.
        var currentVersion = VersionHelper.GetVersion();
        var release = new GitHubReleaseInfo(
            currentVersion, $"Release {currentVersion}", "notes",
            new[] { new GitHubReleaseAssetInfo("twig-win-x64.zip", "https://example.com/dl", 1024) });

        var stubService = new StubReleaseService(latestRelease: release);
        var stubUpdater = new SelfUpdater(new HttpClient());
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_NewerVersionButNoPlatformBinary_Returns1()
    {
        // Supply a release newer than current (which is "0.0.0" in test) with no matching asset
        var release = new GitHubReleaseInfo(
            "v99.99.99", "Release 99.99.99", "notes",
            new[] { new GitHubReleaseAssetInfo("twig-fake-platform.zip", "https://example.com/dl", 1024) });

        var stubService = new StubReleaseService(latestRelease: release);
        var stubUpdater = new SelfUpdater(new HttpClient());
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkError_Returns1()
    {
        var stubService = new StubReleaseService(throwOnGet: new HttpRequestException("Network error"));
        var stubUpdater = new SelfUpdater(new HttpClient());
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(1);
    }

    // ── Stub implementation ────────────────────────────────────────────

    private sealed class StubReleaseService : IGitHubReleaseService
    {
        private readonly GitHubReleaseInfo? _latestRelease;
        private readonly Exception? _throwOnGet;

        public StubReleaseService(GitHubReleaseInfo? latestRelease = null, Exception? throwOnGet = null)
        {
            _latestRelease = latestRelease;
            _throwOnGet = throwOnGet;
        }

        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            return Task.FromResult(_latestRelease);
        }

        public Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(int count, CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            IReadOnlyList<GitHubReleaseInfo> list = _latestRelease is not null
                ? new[] { _latestRelease }
                : Array.Empty<GitHubReleaseInfo>();
            return Task.FromResult(list);
        }
    }
}
