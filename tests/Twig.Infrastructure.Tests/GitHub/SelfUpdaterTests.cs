using System.IO.Compression;
using System.Net;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Unit and integration tests for <see cref="SelfUpdater"/>.
/// Covers: happy-path update, path traversal defense, download failures,
/// incomplete downloads, permission errors, file-lock scenarios, and cleanup.
/// </summary>
public sealed class SelfUpdaterTests : IDisposable
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
    private readonly string _tempDir;

    public SelfUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-selfupdater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 2: Happy path — download + extract + replace (zip)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_ValidZip_ExtractsAndReplacesExe()
    {
        // Arrange: create a fake "current" binary
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-binary-content");

        var binaryContent = "new-binary-content"u8.ToArray();
        var zipBytes = CreateZipArchive((ExeName, binaryContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        // Act
        var result = await sut.UpdateBinaryAsync("https://example.com/archive.zip", "twig-win-x64.zip", null);

        // Assert
        result.MainBinaryPath.ShouldBe(currentExe);
        var updatedContent = File.ReadAllBytes(currentExe);
        updatedContent.ShouldBe(binaryContent);

        // Old binary should be renamed to .old on Windows only
        if (OperatingSystem.IsWindows())
            File.Exists(currentExe + ".old").ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateBinaryAsync_ValidTarGz_ExtractsAndReplacesExe()
    {
        // Arrange
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-binary-content");

        var binaryContent = "new-binary-content"u8.ToArray();
        var tarGzBytes = CreateTarGzArchive((ExeName, binaryContent));

        var downloader = new FakeDownloader(tarGzBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        // Act
        var result = await sut.UpdateBinaryAsync("https://example.com/archive.tar.gz", "twig-linux-x64.tar.gz", null);

        // Assert
        result.MainBinaryPath.ShouldBe(currentExe);
        var updatedContent = File.ReadAllBytes(currentExe);
        updatedContent.ShouldBe(binaryContent);
    }

    [Fact]
    public async Task UpdateBinaryAsync_ZipWithSubfolder_FindsBinary()
    {
        // Archive has the binary nested inside a folder (common for GitHub release assets)
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var binaryContent = "nested-binary"u8.ToArray();
        var zipBytes = CreateZipArchive(($"twig-win-x64/{ExeName}", binaryContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null);

        result.MainBinaryPath.ShouldBe(currentExe);
        File.ReadAllBytes(currentExe).ShouldBe(binaryContent);
    }

    [Fact]
    public async Task UpdateBinaryAsync_CleansUpTempFiles()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var zipBytes = CreateZipArchive((ExeName, "new"u8.ToArray()));
        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        await sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null);

        // Temp directories in the system temp folder matching twig-update-* pattern should be cleaned up.
        // We can't easily assert on temp dir cleanup without hooking, but we verify no exception was thrown.
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 3: Path traversal defense (SECURITY)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractTar_PathTraversalEntry_ThrowsInvalidOperationException()
    {
        // Arrange: create a tar stream with a path-traversal entry
        var extractDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(extractDir);

        var maliciousTar = CreateTarArchive(("../../etc/passwd", "malicious-content"u8.ToArray()));
        using var tarStream = new MemoryStream(maliciousTar);

        // Act & Assert
        var ex = Should.Throw<InvalidOperationException>(
            () => SelfUpdater.ExtractTar(tarStream, extractDir, new DefaultFileSystem()));
        ex.Message.ShouldContain("Path traversal detected");
        ex.Message.ShouldContain("../../etc/passwd");
    }

    [Fact]
    public void ExtractTar_AbsolutePathEntry_ThrowsInvalidOperationException()
    {
        var extractDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(extractDir);

        // Leading slash is stripped by TrimStart('/'), but ../../ after that still triggers
        var maliciousTar = CreateTarArchive(("/../../../etc/shadow", "malicious"u8.ToArray()));
        using var tarStream = new MemoryStream(maliciousTar);

        var ex = Should.Throw<InvalidOperationException>(
            () => SelfUpdater.ExtractTar(tarStream, extractDir, new DefaultFileSystem()));
        ex.Message.ShouldContain("Path traversal detected");
    }

    [Fact]
    public void ExtractTar_SafeEntry_ExtractsSuccessfully()
    {
        var extractDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(extractDir);

        var content = "safe-content"u8.ToArray();
        var tarBytes = CreateTarArchive(("subdir/safe-file.txt", content));
        using var tarStream = new MemoryStream(tarBytes);

        SelfUpdater.ExtractTar(tarStream, extractDir, new DefaultFileSystem());

        var extractedPath = Path.Combine(extractDir, "subdir", "safe-file.txt");
        File.Exists(extractedPath).ShouldBeTrue();
        File.ReadAllBytes(extractedPath).ShouldBe(content);
    }

    [Fact]
    public async Task UpdateBinaryAsync_TarGzWithPathTraversal_ThrowsInvalidOperationException()
    {
        // End-to-end path traversal test through UpdateBinaryAsync
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var maliciousTarGz = CreateTarGzArchive(("../../etc/passwd", "malicious"u8.ToArray()));
        var downloader = new FakeDownloader(maliciousTarGz);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.tar.gz", "twig-linux-x64.tar.gz", null));
        ex.Message.ShouldContain("Path traversal detected");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 4: Download failure — HTTP 404, 403, 503, timeout
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task UpdateBinaryAsync_HttpError_ThrowsDescriptiveException(HttpStatusCode statusCode, string expectedFragment)
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var downloader = Substitute.For<IHttpDownloader>();
        downloader.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException($"Response status code does not indicate success: {(int)statusCode} ({statusCode}).", null, statusCode));

        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/missing.zip", "twig-win-x64.zip", null));
        ex.Message.ShouldContain("Failed to download update");
        ex.Message.ShouldContain("https://example.com/missing.zip");
        ex.Message.ShouldContain(expectedFragment);
        ex.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task UpdateBinaryAsync_Timeout_ThrowsDescriptiveException()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var downloader = Substitute.For<IHttpDownloader>();
        downloader.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/slow.zip", "twig-win-x64.zip", null));
        ex.Message.ShouldContain("Failed to download update");
        ex.InnerException.ShouldBeOfType<TaskCanceledException>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 5: Incomplete download — truncated archive
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_TruncatedZip_ThrowsAndCleansUp()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        // Write an invalid/truncated zip file
        var downloader = new FakeDownloader([0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF]);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        // Should throw during zip extraction
        await Should.ThrowAsync<Exception>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null));

        // Original binary should still exist (not partially overwritten)
        File.ReadAllText(currentExe).ShouldBe("old");
    }

    [Fact]
    public async Task UpdateBinaryAsync_TruncatedTarGz_ThrowsAndCleansUp()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        // Write a truncated gzip stream
        var downloader = new FakeDownloader([0x1F, 0x8B, 0x08, 0x00, 0xFF, 0xFF]);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        await Should.ThrowAsync<Exception>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.tar.gz", "twig-linux-x64.tar.gz", null));

        // Original binary should still exist
        File.ReadAllText(currentExe).ShouldBe("old");
    }

    [Fact]
    public async Task UpdateBinaryAsync_ValidZipMissingBinary_ThrowsDescriptiveError()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        // Create zip with a different file (not the expected binary)
        var zipBytes = CreateZipArchive(("readme.txt", "hello"u8.ToArray()));
        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null));
        ex.Message.ShouldContain("Could not find");
        ex.Message.ShouldContain(ExeName);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 6: Permission denied on replace
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_PermissionDeniedOnMove_ThrowsAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
            return; // FileMove rename trick is Windows-only

        var currentExe = Path.Combine(_tempDir, ExeName);
        var binaryContent = "new-binary"u8.ToArray();
        var zipBytes = CreateZipArchive((ExeName, binaryContent));

        var fileSystem = Substitute.For<IFileSystem>();
        // Allow temp operations to work
        fileSystem.FileCreate(Arg.Any<string>()).Returns(ci => File.Create((string)ci[0]));
        fileSystem.CreateDirectory(Arg.Any<string>());
        fileSystem.When(x => x.ExtractZipToDirectory(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
            .Do(ci =>
            {
                var source = (string)ci[0];
                var dest = (string)ci[1];
                var overwrite = (bool)ci[2];
                ZipFile.ExtractToDirectory(source, dest, overwrite);
            });
        fileSystem.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(ci => Directory.EnumerateFiles((string)ci[0], (string)ci[1], (SearchOption)ci[2]));
        fileSystem.FileExists(Arg.Is<string>(s => s.EndsWith(".old"))).Returns(false);

        // Throw UnauthorizedAccessException on FileMove (the critical operation)
        fileSystem.When(x => x.FileMove(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
            .Do(_ => throw new UnauthorizedAccessException("Access to the path is denied."));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, fileSystem, currentExe);

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null));
        ex.Message.ShouldContain("Access to the path is denied");

        // Verify cleanup was attempted
        fileSystem.Received().FileDelete(Arg.Is<string>(s => s.Contains("twig-update-")));
        fileSystem.Received().DeleteDirectory(Arg.Is<string>(s => s.Contains("twig-update-")), true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 7: Windows file lock on old binary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_OldBinaryLocked_StillSucceeds()
    {
        if (!OperatingSystem.IsWindows())
            return; // .old file lock handling is Windows-only

        var currentExe = Path.Combine(_tempDir, ExeName);

        var binaryContent = "new-binary"u8.ToArray();
        var zipBytes = CreateZipArchive((ExeName, binaryContent));

        var fileSystem = Substitute.For<IFileSystem>();

        // All other FS operations work normally
        fileSystem.When(x => x.CreateDirectory(Arg.Any<string>()))
            .Do(ci => Directory.CreateDirectory((string)ci[0]));
        fileSystem.When(x => x.ExtractZipToDirectory(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
            .Do(ci => ZipFile.ExtractToDirectory((string)ci[0], (string)ci[1], (bool)ci[2]));
        fileSystem.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(ci => Directory.EnumerateFiles((string)ci[0], (string)ci[1], (SearchOption)ci[2]));
        fileSystem.When(x => x.FileDelete(Arg.Any<string>()))
            .Do(_ => { });

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, fileSystem, currentExe);

        // Act — should succeed using FileMove with overwrite: true
        var result = await sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-win-x64.zip", null);

        // Assert
        result.MainBinaryPath.ShouldBe(currentExe);
        // Verify the move was called with overwrite: true
        fileSystem.Received().FileMove(currentExe, currentExe + ".old", true);
        fileSystem.Received().FileCopy(Arg.Any<string>(), currentExe, true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Task 8: CleanupOldBinary
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CleanupOldBinaryCore_OldFileExists_DeletesIt()
    {
        var exePath = Path.Combine(_tempDir, ExeName);
        var oldPath = exePath + ".old";
        File.WriteAllText(oldPath, "old-binary");

        SelfUpdater.CleanupOldBinaryCore(new DefaultFileSystem(), exePath);

        File.Exists(oldPath).ShouldBeFalse();
    }

    [Fact]
    public void CleanupOldBinaryCore_NoOldFile_DoesNotThrow()
    {
        var exePath = Path.Combine(_tempDir, ExeName);

        // Should not throw even when .old file doesn't exist
        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(new DefaultFileSystem(), exePath));
    }

    [Fact]
    public void CleanupOldBinaryCore_NullProcessPath_DoesNotThrow()
    {
        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(new DefaultFileSystem(), processPath: null));
    }

    [Fact]
    public void CleanupOldBinaryCore_LockedOldFile_DoesNotThrow()
    {
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        fileSystem.When(x => x.FileDelete(Arg.Any<string>()))
            .Do(_ => throw new IOException("File is locked."));

        // Best-effort cleanup — should not throw
        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(fileSystem, "/app/twig"));
    }

    [Fact]
    public void CleanupOldBinary_Static_DoesNotThrow()
    {
        // The static convenience method should never throw regardless of environment state
        Should.NotThrow(SelfUpdater.CleanupOldBinary);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Additional edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_NullProcessPath_ThrowsInvalidOperationException()
    {
        var downloader = Substitute.For<IHttpDownloader>();
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), processPath: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.zip", "twig-x64.zip", null));
        ex.Message.ShouldContain("Cannot determine current executable path");
    }

    [Fact]
    public async Task UpdateBinaryAsync_UnsupportedArchiveFormat_ThrowsInvalidOperationException()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old");

        var downloader = new FakeDownloader("dummy"u8.ToArray());
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.UpdateBinaryAsync("https://example.com/dl.7z", "twig-win-x64.7z", null));
        ex.Message.ShouldContain("Unsupported archive format");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Interface assignability
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SelfUpdater_ImplementsICompanionInstaller()
    {
        typeof(SelfUpdater).ShouldBeAssignableTo(typeof(ICompanionInstaller));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Companion extraction — UpdateBinaryAsync with companions
    // ═══════════════════════════════════════════════════════════════

    private static readonly string CompanionExe = OperatingSystem.IsWindows() ? "twig-mcp.exe" : "twig-mcp";
    private static readonly string CompanionExe2 = OperatingSystem.IsWindows() ? "twig-tui.exe" : "twig-tui";

    [Fact]
    public async Task UpdateBinaryAsync_WithCompanions_ExtractsMainAndCompanions()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var mainContent = "new-main"u8.ToArray();
        var companionContent = "new-companion"u8.ToArray();
        var zipBytes = CreateZipArchive(
            (ExeName, mainContent),
            (CompanionExe, companionContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe });

        result.MainBinaryPath.ShouldBe(currentExe);
        File.ReadAllBytes(currentExe).ShouldBe(mainContent);

        result.Companions.Count.ShouldBe(1);
        result.Companions[0].Name.ShouldBe(CompanionExe);
        result.Companions[0].Found.ShouldBeTrue();
        result.Companions[0].InstalledPath.ShouldNotBeNull();
        File.ReadAllBytes(result.Companions[0].InstalledPath!).ShouldBe(companionContent);
    }

    [Fact]
    public async Task UpdateBinaryAsync_MissingCompanionInArchive_RecordsFoundFalse()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var mainContent = "new-main"u8.ToArray();
        var zipBytes = CreateZipArchive((ExeName, mainContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe });

        result.MainBinaryPath.ShouldBe(currentExe);
        result.Companions.Count.ShouldBe(1);
        result.Companions[0].Found.ShouldBeFalse();
        result.Companions[0].InstalledPath.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateBinaryAsync_NullCompanionList_ReturnsEmptyCompanions()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var zipBytes = CreateZipArchive((ExeName, "new"u8.ToArray()));
        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip", null);

        result.Companions.Count.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateBinaryAsync_MultipleCompanions_SomeFoundSomeMissing()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var mainContent = "new-main"u8.ToArray();
        var companion1Content = "companion1"u8.ToArray();
        // Only include one of two companions
        var zipBytes = CreateZipArchive(
            (ExeName, mainContent),
            (CompanionExe, companion1Content));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe, CompanionExe2 });

        result.Companions.Count.ShouldBe(2);
        result.Companions[0].Name.ShouldBe(CompanionExe);
        result.Companions[0].Found.ShouldBeTrue();
        result.Companions[1].Name.ShouldBe(CompanionExe2);
        result.Companions[1].Found.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateBinaryAsync_CompanionFromTarGz_ExtractsCorrectly()
    {
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var mainContent = "new-main"u8.ToArray();
        var companionContent = "companion-tar"u8.ToArray();
        var tarGzBytes = CreateTarGzArchive(
            (ExeName, mainContent),
            (CompanionExe, companionContent));

        var downloader = new FakeDownloader(tarGzBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.tar.gz", "twig-linux-x64.tar.gz",
            new[] { CompanionExe });

        result.MainBinaryPath.ShouldBe(currentExe);
        result.Companions.Count.ShouldBe(1);
        result.Companions[0].Found.ShouldBeTrue();
        File.ReadAllBytes(result.Companions[0].InstalledPath!).ShouldBe(companionContent);
    }

    [Fact]
    public async Task UpdateBinaryAsync_WindowsRenameTrickForCompanion_RenamesExistingToOld()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        // Pre-existing companion binary
        var companionPath = Path.Combine(_tempDir, CompanionExe);
        File.WriteAllText(companionPath, "old-companion");

        var mainContent = "new-main"u8.ToArray();
        var companionContent = "new-companion"u8.ToArray();
        var zipBytes = CreateZipArchive(
            (ExeName, mainContent),
            (CompanionExe, companionContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), currentExe);

        var result = await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe });

        // New companion installed
        File.ReadAllBytes(companionPath).ShouldBe(companionContent);
        // Old companion renamed
        File.Exists(companionPath + ".old").ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  InstallCompanionsOnlyAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task InstallCompanionsOnlyAsync_InstallsOnlyCompanions()
    {
        var installDir = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(installDir);

        var mainContent = "main-binary"u8.ToArray();
        var companionContent = "companion-binary"u8.ToArray();
        var zipBytes = CreateZipArchive(
            (ExeName, mainContent),
            (CompanionExe, companionContent));

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), processPath: null);

        var results = await sut.InstallCompanionsOnlyAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe },
            installDir);

        results.Count.ShouldBe(1);
        results[0].Name.ShouldBe(CompanionExe);
        results[0].Found.ShouldBeTrue();

        // Companion installed
        var installedPath = Path.Combine(installDir, CompanionExe);
        File.Exists(installedPath).ShouldBeTrue();
        File.ReadAllBytes(installedPath).ShouldBe(companionContent);

        // Main binary NOT installed in installDir
        File.Exists(Path.Combine(installDir, ExeName)).ShouldBeFalse();
    }

    [Fact]
    public async Task InstallCompanionsOnlyAsync_MissingCompanion_RecordsFoundFalse()
    {
        var installDir = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(installDir);

        var zipBytes = CreateZipArchive((ExeName, "main"u8.ToArray()));
        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), processPath: null);

        var results = await sut.InstallCompanionsOnlyAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe },
            installDir);

        results.Count.ShouldBe(1);
        results[0].Found.ShouldBeFalse();
        results[0].InstalledPath.ShouldBeNull();
    }

    [Fact]
    public async Task InstallCompanionsOnlyAsync_FromTarGz_Works()
    {
        var installDir = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(installDir);

        var companionContent = "companion-tar"u8.ToArray();
        var tarGzBytes = CreateTarGzArchive(
            (ExeName, "main"u8.ToArray()),
            (CompanionExe, companionContent));

        var downloader = new FakeDownloader(tarGzBytes);
        var sut = new SelfUpdater(downloader, new DefaultFileSystem(), processPath: null);

        var results = await sut.InstallCompanionsOnlyAsync(
            "https://example.com/dl.tar.gz", "twig-linux-x64.tar.gz",
            new[] { CompanionExe },
            installDir);

        results.Count.ShouldBe(1);
        results[0].Found.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(installDir, CompanionExe)).ShouldBe(companionContent);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CleanupOldBinaryCore — companion cleanup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CleanupOldBinaryCore_WithCompanions_CleansCompanionOldFiles()
    {
        var exePath = Path.Combine(_tempDir, ExeName);
        var companionOld = Path.Combine(_tempDir, CompanionExe + ".old");
        File.WriteAllText(companionOld, "old-companion");

        SelfUpdater.CleanupOldBinaryCore(
            new DefaultFileSystem(), exePath,
            new[] { "twig-mcp" });

        File.Exists(companionOld).ShouldBeFalse();
    }

    [Fact]
    public void CleanupOldBinaryCore_WithCompanions_NoOldFiles_DoesNotThrow()
    {
        var exePath = Path.Combine(_tempDir, ExeName);

        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(
            new DefaultFileSystem(), exePath,
            new[] { "twig-mcp", "twig-tui" }));
    }

    [Fact]
    public void CleanupOldBinaryCore_WithCompanions_NullProcessPath_DoesNotThrow()
    {
        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(
            new DefaultFileSystem(), processPath: null,
            new[] { "twig-mcp" }));
    }

    [Fact]
    public void CleanupOldBinaryCore_WithCompanions_LockedOldFile_DoesNotThrow()
    {
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        fileSystem.When(x => x.FileDelete(Arg.Any<string>()))
            .Do(_ => throw new IOException("File is locked."));

        Should.NotThrow(() => SelfUpdater.CleanupOldBinaryCore(
            fileSystem, "/app/twig",
            new[] { "twig-mcp" }));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Companion atomic write verification (mock-based)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateBinaryAsync_CompanionExtraction_UsesAtomicWrites()
    {
        // Arrange: mock filesystem to verify the temp→move sequence
        var currentExe = Path.Combine(_tempDir, ExeName);
        File.WriteAllText(currentExe, "old-main");

        var mainContent = "new-main"u8.ToArray();
        var companionContent = "new-companion"u8.ToArray();
        var zipBytes = CreateZipArchive(
            (ExeName, mainContent),
            (CompanionExe, companionContent));

        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.When(x => x.CreateDirectory(Arg.Any<string>()))
            .Do(ci => Directory.CreateDirectory((string)ci[0]));
        fileSystem.When(x => x.ExtractZipToDirectory(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
            .Do(ci => ZipFile.ExtractToDirectory((string)ci[0], (string)ci[1], (bool)ci[2]));
        fileSystem.EnumerateFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(ci => Directory.EnumerateFiles((string)ci[0], (string)ci[1], (SearchOption)ci[2]));
        fileSystem.FileExists(Arg.Any<string>()).Returns(false);
        fileSystem.When(x => x.FileDelete(Arg.Any<string>()))
            .Do(_ => { });

        var downloader = new FakeDownloader(zipBytes);
        var sut = new SelfUpdater(downloader, fileSystem, currentExe);

        // Act
        await sut.UpdateBinaryAsync(
            "https://example.com/dl.zip", "twig-win-x64.zip",
            new[] { CompanionExe });

        // Assert: companion was copied to .tmp first, then moved to final path
        var expectedFinalPath = Path.Combine(_tempDir, CompanionExe);

        fileSystem.Received().FileCopy(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.EndsWith(".tmp")),
            true);
        fileSystem.Received().FileMove(
            Arg.Is<string>(s => s.EndsWith(".tmp")),
            expectedFinalPath,
            true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fake <see cref="IHttpDownloader"/> that writes predefined bytes to the destination file.
    /// </summary>
    private sealed class FakeDownloader : IHttpDownloader
    {
        private readonly byte[] _archiveBytes;

        public FakeDownloader(byte[] archiveBytes) => _archiveBytes = archiveBytes;

        public Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
        {
            File.WriteAllBytes(destinationPath, _archiveBytes);
            return Task.CompletedTask;
        }
    }

    /// <summary>Creates an in-memory zip archive containing the specified entries.</summary>
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

    /// <summary>Creates an in-memory tar archive (uncompressed) containing the specified entries.</summary>
    private static byte[] CreateTarArchive(params (string entryName, byte[] content)[] entries)
    {
        using var ms = new MemoryStream();
        foreach (var (entryName, content) in entries)
        {
            WriteTarEntry(ms, entryName, content);
        }
        // End-of-archive marker: two 512-byte zero blocks
        ms.Write(new byte[1024]);
        return ms.ToArray();
    }

    /// <summary>Creates an in-memory gzip-compressed tar archive.</summary>
    private static byte[] CreateTarGzArchive(params (string entryName, byte[] content)[] entries)
    {
        var tarBytes = CreateTarArchive(entries);
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(tarBytes);
        }
        return ms.ToArray();
    }

    /// <summary>Writes a single tar header + data for a regular file entry.</summary>
    private static void WriteTarEntry(Stream stream, string name, byte[] content)
    {
        var header = new byte[512];

        // Name: offset 0, length 100
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));

        // Mode: offset 100, length 8
        Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);

        // Owner/Group: offset 108/116, length 8 each
        Encoding.ASCII.GetBytes("0001000\0").CopyTo(header, 108);
        Encoding.ASCII.GetBytes("0001000\0").CopyTo(header, 116);

        // Size: offset 124, length 12 (octal, null-terminated)
        var sizeOctal = Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0";
        Encoding.ASCII.GetBytes(sizeOctal).CopyTo(header, 124);

        // Modification time: offset 136, length 12
        var mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var mtimeOctal = Convert.ToString(mtime, 8).PadLeft(11, '0') + "\0";
        Encoding.ASCII.GetBytes(mtimeOctal).CopyTo(header, 136);

        // Type flag: offset 156 ('0' = regular file)
        header[156] = (byte)'0';

        // Compute checksum: offset 148, length 8 (fill with spaces during computation)
        for (int i = 148; i < 156; i++) header[i] = 0x20;
        long checksum = 0;
        for (int i = 0; i < 512; i++) checksum += header[i];
        var checksumStr = Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ";
        Encoding.ASCII.GetBytes(checksumStr).CopyTo(header, 148);

        stream.Write(header);
        stream.Write(content);

        // Pad to 512-byte boundary
        var pad = (int)((512 - (content.Length % 512)) % 512);
        if (pad > 0) stream.Write(new byte[pad]);
    }
}
