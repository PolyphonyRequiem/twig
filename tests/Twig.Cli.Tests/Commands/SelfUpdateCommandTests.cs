using System.IO.Compression;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SelfUpdateCommandTests : IDisposable
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
    private readonly string _tempDir;

    public SelfUpdateCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-cmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

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
            new[] { new GitHubReleaseAssetInfo("twig-fake-platform.zip", "https://example.com/dl", 1024) });

        var stubService = new StubReleaseService(latestRelease: release);
        var stubUpdater = new SelfUpdater(new HttpClient());
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyUpToDate_CompanionInstallFailure_Returns0()
    {
        // When up to date and companion install fails (download error),
        // the command still returns 0 — companion install failures are non-fatal.
        // Uses a ThrowingDownloader to avoid real HTTP calls in CI.
        var currentVersion = VersionHelper.GetVersion();
        var rid = PlatformHelper.DetectRid() ?? "win-x64";
        var ext = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var assetName = $"twig-{rid}{ext}";

        var release = new GitHubReleaseInfo(
            currentVersion, $"Release {currentVersion}", "notes", null,
            new[] { new GitHubReleaseAssetInfo(assetName, "https://example.com/dl", 1024) });

        var stubService = new StubReleaseService(latestRelease: release);
        var stubUpdater = new SelfUpdater(
            new ThrowingDownloader(), new DefaultFileSystem(), Environment.ProcessPath);
        var command = new SelfUpdateCommand(stubService, stubUpdater);

        var exitCode = await command.ExecuteAsync();

        // Non-fatal: companion install failure does not affect the exit code
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyUpToDate_InstallsMissingCompanions_ReportsResults()
    {
        // F3: when main binary is already current and companions are missing,
        // they get installed and per-companion results appear in console output.
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "current");

        var companionExeNames = CompanionTools.All
            .Select(CompanionTools.GetExeName)
            .ToArray();

        // Build a zip archive containing companion binaries (no main binary needed)
        var zipBytes = CreateZipArchive(
            [.. companionExeNames.Select(c => (c, "companion"u8.ToArray()))]);

        var currentVersion = VersionHelper.GetVersion();
        var rid = PlatformHelper.DetectRid() ?? "win-x64";
        var ext = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var assetName = $"twig-{rid}{ext}";

        var release = new GitHubReleaseInfo(
            currentVersion, $"Release {currentVersion}", "notes", null,
            new[] { new GitHubReleaseAssetInfo(assetName, "https://example.com/dl", 1024) });

        var selfUpdater = new SelfUpdater(
            new FakeDownloader(zipBytes), new DefaultFileSystem(), currentExe);
        var command = new SelfUpdateCommand(
            new StubReleaseService(latestRelease: release), selfUpdater);

        // Act: capture stdout to verify companion result output
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exitCode = await command.ExecuteAsync();

            exitCode.ShouldBe(0);
            var output = sw.ToString();

            // ReportCompanionResults should have printed a line per companion
            foreach (var c in companionExeNames)
                output.ShouldContain(c);
            output.ShouldContain("installed");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
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

    // ── F2: companion results reported on upgrade ─────────────────────

    [Fact]
    public async Task ExecuteAsync_NewerVersion_ReportsCompanionResults()
    {
        // Arrange: create a fake "current" binary in a temp dir
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var companionExeNames = CompanionTools.All
            .Select(CompanionTools.GetExeName)
            .ToArray();

        // Build a zip archive containing main binary + all companions
        var zipBytes = CreateZipArchive(
            [(ExeName, "new-binary"u8.ToArray()),
             .. companionExeNames.Select(c => (c, "companion"u8.ToArray()))]);

        var rid = PlatformHelper.DetectRid() ?? "win-x64";
        var ext = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var assetName = $"twig-{rid}{ext}";

        var selfUpdater = new SelfUpdater(
            new FakeDownloader(zipBytes), new DefaultFileSystem(), currentExe);

        var release = new GitHubReleaseInfo(
            "v99.99.99", "Release 99.99.99", "Release notes body", null,
            new[] { new GitHubReleaseAssetInfo(assetName, "https://example.com/dl", 1024) });

        var command = new SelfUpdateCommand(
            new StubReleaseService(latestRelease: release), selfUpdater);

        // Act: capture stdout to verify companion result output
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exitCode = await command.ExecuteAsync();

            exitCode.ShouldBe(0);
            var output = sw.ToString();

            // ReportCompanionResults should have printed a line per companion
            foreach (var c in companionExeNames)
                output.ShouldContain(c);
            output.ShouldContain("installed");

            // Verify release notes were also printed (proves we reached past ReportCompanionResults)
            output.ShouldContain("Release notes body");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
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
            return Task.FromResult<IReadOnlyList<GitHubReleaseInfo>>(
                _latestRelease is not null ? [_latestRelease] : []);
        }

        public Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string tag, CancellationToken ct = default)
        {
            if (_throwOnGet is not null) throw _throwOnGet;
            return Task.FromResult(_latestRelease?.Tag == tag ? _latestRelease : (GitHubReleaseInfo?)null);
        }
    }

    // ── Test helpers ───────────────────────────────────────────────────

    private sealed class FakeDownloader(byte[] archiveBytes) : IHttpDownloader
    {
        public Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
        {
            File.WriteAllBytes(destinationPath, archiveBytes);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDownloader : IHttpDownloader
    {
        public Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
            => throw new HttpRequestException("Simulated download failure");
    }

    private static byte[] CreateZipArchive(params (string entryName, byte[] content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                var entry = archive.CreateEntry(entryName);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }
        return ms.ToArray();
    }
}
