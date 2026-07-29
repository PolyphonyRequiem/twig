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
        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        await _companionInstaller.Received(1).InstallCompanionsOnlyAsync(
            "https://example.com/twig-test.zip",
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 2),
            Dir,
            Arg.Any<CancellationToken>());
        _fileSystem.Received(1).FileCreate(VersionFile);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureCompanionsAsync_InstallerThrows_WritesVersionMarker(bool cancelled)
    {
        SetupMissingCompanions("twig-mcp");
        SetupRelease();

        Exception ex = cancelled
            ? new OperationCanceledException("Timed out")
            : new InvalidOperationException("Network error");

        _companionInstaller.InstallCompanionsOnlyAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ex);

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        _fileSystem.Received(1).FileCreate(VersionFile);
    }

    [Fact]
    public async Task EnsureCompanionsAsync_NoReleaseFound_WritesVersionMarker()
    {
        SetupMissingCompanions("twig-mcp");
        _releaseService.GetReleaseByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GitHubReleaseInfo?)null);

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        // Version marker is still written despite release not found
        _fileSystem.Received(1).FileCreate(VersionFile);
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
    //  TICKET-0311 — dotnet-hosted launches must never take the slow path
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Process paths that name a generic host rather than the twig apphost. Every one of
    /// these appears as <c>Environment.ProcessPath</c> when twig is launched
    /// framework-dependent (<c>dotnet path/to/twig.dll</c>) — which is exactly how the
    /// Cli test suite spawns it.
    /// </summary>
    public static TheoryData<string> HostProcessPaths() =>
        new(
            Path.Combine(Dir, $"dotnet{ExeExt}"),
            Path.Combine(Path.GetTempPath(), "some-sdk", $"dotnet{ExeExt}"));

    [Theory]
    [MemberData(nameof(HostProcessPaths))]
    public async Task EnsureCompanionsAsync_DotnetHostedLaunch_MakesNoNetworkCall(string hostPath)
    {
        // Fixture precondition, asserted explicitly: companions are MISSING and no version
        // marker exists, so on the unfixed code this reaches Phase 3 and calls GitHub with a
        // 60 s budget. Without this guard the test could silently degrade into the Phase 1
        // "all companions present" happy path and prove nothing.
        SetupMissingCompanions("twig-mcp", "twig-tui");
        SetupSuccessfulDownload();
        _fileSystem.FileExists(Path.Combine(Dir, CompanionTools.GetExeName("twig-mcp")))
            .ShouldBeFalse("fixture must present a MISSING companion or the slow path never runs");

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(hostPath, CurrentVersion);

        // The network call is the thing that hung the suite for ~275 s. It must not happen.
        _releaseService.ReceivedCalls().ShouldBeEmpty(
            "TICKET-0311: a `dotnet twig.dll` launch must not reach GitHub. Companions live "
            + "next to the twig apphost, so the dotnet host's directory is never an install "
            + "dir — yet the pre-fix code treated it as one and paid a blocking 60 s call on "
            + "every spawned-CLI test, blowing the 300 s vstest wall.");
        _companionInstaller.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(HostProcessPaths))]
    public async Task EnsureCompanionsAsync_DotnetHostedLaunch_WritesNoMarkerIntoHostDirectory(
        string hostPath)
    {
        SetupMissingCompanions("twig-mcp", "twig-tui");
        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(hostPath, CurrentVersion);

        // The pre-fix code littered a `.twig-version` file into whatever directory hosted
        // the process — in practice the .NET SDK install folder.
        _fileSystem.DidNotReceive().FileCreate(Arg.Any<string>());
    }

    [Fact]
    public async Task EnsureCompanionsAsync_RealTwigApphost_StillTakesTheSlowPath()
    {
        // Positive control. Without this, the two tests above would still pass if the guard
        // disabled the companion check entirely, which would silently break real upgrades.
        SetupMissingCompanions("twig-mcp");
        SetupSuccessfulDownload();

        var sut = CreateSut();
        await sut.EnsureCompanionsAsync(ProcessPath, CurrentVersion);

        await _companionInstaller.Received(1).InstallCompanionsOnlyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Dir, Arg.Any<CancellationToken>());
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
            .Returns(Task.FromResult<IReadOnlyList<CompanionUpdateResult>>([]));
    }
}
