using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="SemVerComparer"/> and <see cref="SelfUpdateCommand.ExecuteAsync"/> command flow.
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
            currentVersion, $"Release {currentVersion}", "notes", null,
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
            "v99.99.99", "Release 99.99.99", "notes", null,
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
