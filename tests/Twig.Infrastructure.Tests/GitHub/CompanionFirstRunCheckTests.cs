using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="CompanionFirstRunCheck"/>.
/// All file I/O is mocked via <see cref="IFileSystem"/>.
/// </summary>
public sealed class CompanionFirstRunCheckTests
{
    private static readonly string ExeExt = OperatingSystem.IsWindows() ? ".exe" : "";
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "twig-frc-test");
    private static readonly string ProcessPath = Path.Combine(Dir, $"twig{ExeExt}");
    private static readonly string VersionFile = Path.Combine(Dir, ".twig-version");
    private const string CurrentVersion = "1.5.0";

    private readonly IGitHubReleaseService _releaseService = Substitute.For<IGitHubReleaseService>();
    private readonly ICompanionInstaller _companionInstaller = Substitute.For<ICompanionInstaller>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();

    private CompanionFirstRunCheck CreateSut() => new(_releaseService, _companionInstaller, _fileSystem);

    // ═══════════════════════════════════════════════════════════════
    //  Phase 1 — Fast path (no I/O write)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureCompanionsAsync_NullProcessPath_ReturnsImmediately()
    {
        var sut = CreateSut();

        await sut.EnsureCompanionsAsync(null, CurrentVersion);

        _releaseService.ReceivedCalls().ShouldBeEmpty();
        _companionInstaller.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureCompanionsAsync_AllCompanionsPresent_NoDownload()
    {
        // All companion exe files exist
        _fileSystem.FileExists(Arg.Any<string>()).Returns(callInfo =>
        {
            var path = callInfo.Arg<string>();
            return !path.EndsWith(".twig-version");
        });

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        _releaseService.ReceivedCalls().ShouldBeEmpty();
        _companionInstaller.ReceivedCalls().ShouldBeEmpty();
        // Should NOT write any files (no FileCreate calls)
        _fileSystem.DidNotReceive().FileCreate(Arg.Any<string>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 2 — Version marker check
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureCompanionsAsync_VersionMarkerMatchesCurrent_ReturnsWithoutDownload()
    {
        SetupMissingCompanions("twig-mcp");
        SetupVersionFile(CurrentVersion);

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        _releaseService.ReceivedCalls().ShouldBeEmpty();
        _companionInstaller.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureCompanionsAsync_VersionMarkerOlderVersion_ProceedsToDownload()
    {
        SetupMissingCompanions("twig-mcp");
        SetupVersionFile("1.4.0"); // older version
        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        await _companionInstaller.Received(1).InstallCompanionsOnlyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 3 — Download
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureCompanionsAsync_SuccessfulDownload_InstallsCompanions()
    {
        SetupMissingCompanions("twig-mcp", "twig-tui");
        SetupNoVersionFile();
        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        await _companionInstaller.Received(1).InstallCompanionsOnlyAsync(
            "https://example.com/twig-test.zip",
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 2),
            Dir,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCompanionsAsync_DownloadFails_WritesVersionMarkerAnyway()
    {
        SetupMissingCompanions("twig-mcp");
        SetupNoVersionFile();
        SetupRelease();

        _companionInstaller.InstallCompanionsOnlyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        // Version marker is still written
        _fileSystem.Received(1).FileCreate(VersionFile);
    }

    [Fact]
    public async Task EnsureCompanionsAsync_OperationCanceled_WritesVersionMarker()
    {
        SetupMissingCompanions("twig-mcp");
        SetupNoVersionFile();
        SetupRelease();

        _companionInstaller.InstallCompanionsOnlyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("Timed out"));

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        // Version marker is still written
        _fileSystem.Received(1).FileCreate(VersionFile);
    }

    [Fact]
    public async Task EnsureCompanionsAsync_NoReleaseFound_WritesVersionMarker()
    {
        SetupMissingCompanions("twig-mcp");
        SetupNoVersionFile();
        _releaseService.GetReleaseByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitHubReleaseInfo?)null);

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        // Version marker is still written despite release not found
        _fileSystem.Received(1).FileCreate(VersionFile);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 4 — Version marker write
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureCompanionsAsync_AfterSuccessfulDownload_WritesVersionMarker()
    {
        SetupMissingCompanions("twig-mcp");
        SetupNoVersionFile();
        SetupSuccessfulDownload();

        var memStream = new MemoryStream();
        _fileSystem.FileCreate(VersionFile).Returns(memStream);

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        _fileSystem.Received(1).FileCreate(VersionFile);
        // Verify the version was written
        memStream.ToArray().Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task EnsureCompanionsAsync_OnlyMissingCompanions_AreRequested()
    {
        // twig-mcp exists, twig-tui missing
        var mcpExe = CompanionTools.GetExeName("twig-mcp");
        var tuiExe = CompanionTools.GetExeName("twig-tui");

        _fileSystem.FileExists(Path.Combine(Dir, mcpExe)).Returns(true);
        _fileSystem.FileExists(Path.Combine(Dir, tuiExe)).Returns(false);
        _fileSystem.FileExists(VersionFile).Returns(false);
        _fileSystem.FileCreate(Arg.Any<string>()).Returns(_ => new MemoryStream());

        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        await _companionInstaller.Received(1).InstallCompanionsOnlyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 1 && list[0] == tuiExe),
            Dir,
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void SetupMissingCompanions(params string[] missingBaseNames)
    {
        var missingExeNames = missingBaseNames.Select(CompanionTools.GetExeName).ToHashSet();

        _fileSystem.FileExists(Arg.Any<string>()).Returns(callInfo =>
        {
            var path = callInfo.Arg<string>();
            var fileName = Path.GetFileName(path);

            if (fileName == ".twig-version")
                return false; // default, overridden by SetupVersionFile
            return !missingExeNames.Contains(fileName);
        });

        _fileSystem.FileCreate(Arg.Any<string>()).Returns(_ => new MemoryStream());
    }

    private void SetupVersionFile(string version)
    {
        _fileSystem.FileExists(VersionFile).Returns(true);
        var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(version));
        _fileSystem.FileOpenRead(VersionFile).Returns(ms);
    }

    private void SetupNoVersionFile()
    {
        _fileSystem.FileExists(VersionFile).Returns(false);
    }

    private void SetupRelease()
    {
        var rid = PlatformHelper.DetectRid() ?? "win-x64";
        var ext = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var assetName = $"twig-{rid}{ext}";

        var release = new GitHubReleaseInfo(
            $"v{CurrentVersion}",
            $"v{CurrentVersion}",
            "Release notes",
            DateTimeOffset.UtcNow,
            [new GitHubReleaseAssetInfo(assetName, "https://example.com/twig-test.zip", 1024)]);

        _releaseService.GetReleaseByTagAsync($"v{CurrentVersion}", Arg.Any<CancellationToken>())
            .Returns(release);
    }

    private void SetupSuccessfulDownload()
    {
        SetupRelease();

        _companionInstaller.InstallCompanionsOnlyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var companions = callInfo.Arg<IReadOnlyList<string>>();
                return companions.Select(c => new CompanionUpdateResult(c, true, Path.Combine(Dir, c)))
                    .ToList();
            });
    }
}
