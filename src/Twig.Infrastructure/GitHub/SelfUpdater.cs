using System.IO.Compression;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Downloads and applies self-update binaries from GitHub Releases.
/// Handles platform-specific file-lock strategies (Windows rename trick vs. Unix direct overwrite).
/// Also supports companion binary extraction via <see cref="InstallCompanionsOnlyAsync"/>.
/// </summary>
public sealed class SelfUpdater
{
    private readonly IHttpDownloader _downloader;
    private readonly IFileSystem _fileSystem;
    private readonly string? _processPath;

    public SelfUpdater(HttpClient httpClient)
        : this(new HttpClientDownloader(httpClient), new DefaultFileSystem(), Environment.ProcessPath)
    {
    }

    internal SelfUpdater(IHttpDownloader downloader, IFileSystem fileSystem, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _downloader = downloader;
        _fileSystem = fileSystem;
        _processPath = processPath;
    }

    /// <summary>
    /// Downloads the archive from <paramref name="downloadUrl"/>, extracts the main binary
    /// and any companion binaries, and replaces the current executable.
    /// </summary>
    /// <returns>An <see cref="UpdateResult"/> with the main binary path and per-companion status.</returns>
    public async Task<UpdateResult> UpdateBinaryAsync(
        string downloadUrl,
        string archiveName,
        IReadOnlyList<string>? companionExeNames,
        CancellationToken ct = default)
    {
        var currentExe = _processPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");
        var currentDir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Cannot determine current executable directory.");

        var tempArchive = await DownloadArchiveAsync(downloadUrl, archiveName, ct);
        var tempExtractDir = ExtractArchive(tempArchive, archiveName);
        try
        {
            // Install main binary
            var exeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
            var extractedBinary = FindBinary(tempExtractDir, exeName)
                ?? throw new InvalidOperationException($"Could not find '{exeName}' in downloaded archive.");

            InstallBinaryToDir(extractedBinary, currentExe);

            // Install companions
            var companions = InstallCompanions(tempExtractDir, companionExeNames, currentDir);

            return new UpdateResult(currentExe, companions);
        }
        finally
        {
            CleanupTempFiles(tempArchive, tempExtractDir);
        }
    }

    /// <summary>
    /// Downloads the archive at <paramref name="archiveUrl"/> and extracts only the
    /// companion executables whose names appear in <paramref name="companionExeNames"/>.
    /// </summary>
    public async Task<IReadOnlyList<CompanionUpdateResult>> InstallCompanionsOnlyAsync(
        string archiveUrl,
        string archiveName,
        IReadOnlyList<string> companionExeNames,
        string installDir,
        CancellationToken ct = default)
    {
        var tempArchive = await DownloadArchiveAsync(archiveUrl, archiveName, ct);
        var tempExtractDir = ExtractArchive(tempArchive, archiveName);
        try
        {
            return InstallCompanions(tempExtractDir, companionExeNames, installDir);
        }
        finally
        {
            CleanupTempFiles(tempArchive, tempExtractDir);
        }
    }

    /// <summary>
    /// Cleans up <c>.old</c> binaries left behind from a previous Windows update,
    /// including both the main binary and any companion <c>.old</c> files.
    /// Safe to call on any platform — no-ops if no old binaries exist.
    /// </summary>
    public static void CleanupOldBinary()
    {
        CleanupOldBinaryCore(new DefaultFileSystem(), Environment.ProcessPath, CompanionTools.All);
    }

    /// <summary>
    /// Testable overload of <see cref="CleanupOldBinary"/> that accepts injected dependencies.
    /// </summary>
    internal static void CleanupOldBinaryCore(IFileSystem fileSystem, string? processPath)
    {
        if (processPath is null) return;

        var oldPath = processPath + ".old";
        try
        {
            if (fileSystem.FileExists(oldPath))
                fileSystem.FileDelete(oldPath);
        }
        catch
        {
            // Best-effort cleanup — ignore if the old binary is still locked.
        }
    }

    /// <summary>
    /// Testable overload that also cleans companion <c>.old</c> files in the given directory.
    /// </summary>
    internal static void CleanupOldBinaryCore(IFileSystem fileSystem, string? processPath, IReadOnlyList<string> companionNames)
    {
        CleanupOldBinaryCore(fileSystem, processPath);

        if (processPath is null) return;

        var dir = Path.GetDirectoryName(processPath);
        if (dir is null) return;

        foreach (var companion in companionNames)
        {
            var oldPath = Path.Combine(dir, CompanionTools.GetExeName(companion) + ".old");
            try
            {
                if (fileSystem.FileExists(oldPath))
                    fileSystem.FileDelete(oldPath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Shared helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> DownloadArchiveAsync(string downloadUrl, string archiveName, CancellationToken ct)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), $"twig-update-{Guid.NewGuid():N}{Path.GetExtension(archiveName)}");
        try
        {
            await _downloader.DownloadFileAsync(downloadUrl, tempArchive, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to download update from {downloadUrl}: {ex.Message}", ex);
        }

        return tempArchive;
    }

    private string ExtractArchive(string tempArchive, string archiveName)
    {
        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"twig-update-{Guid.NewGuid():N}");
        _fileSystem.CreateDirectory(tempExtractDir);

        if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            _fileSystem.ExtractZipToDirectory(tempArchive, tempExtractDir, overwriteFiles: true);
        }
        else if (archiveName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGz(tempArchive, tempExtractDir);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported archive format: {archiveName}");
        }

        return tempExtractDir;
    }

    private string? FindBinary(string directory, string binaryName)
    {
        return _fileSystem.EnumerateFiles(directory, binaryName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private void InstallBinaryToDir(string extractedBinary, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows file-lock strategy: rename running exe → .old, copy new exe
            var oldPath = targetPath + ".old";
            _fileSystem.FileMove(targetPath, oldPath, overwrite: true);
            _fileSystem.FileCopy(extractedBinary, targetPath, overwrite: true);
        }
        else
        {
            // Unix: direct overwrite (running binary is not locked)
            _fileSystem.FileCopy(extractedBinary, targetPath, overwrite: true);

            // chmod +x
            _fileSystem.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private IReadOnlyList<CompanionUpdateResult> InstallCompanions(
        string extractDir,
        IReadOnlyList<string>? companionExeNames,
        string installDir)
    {
        if (companionExeNames is null or { Count: 0 })
            return [];

        var results = new List<CompanionUpdateResult>(companionExeNames.Count);
        foreach (var companionExe in companionExeNames)
        {
            var extracted = FindBinary(extractDir, companionExe);
            if (extracted is null)
            {
                results.Add(new CompanionUpdateResult(companionExe, Found: false, InstalledPath: null));
                continue;
            }

            var targetPath = Path.Combine(installDir, companionExe);
            var tempTargetPath = targetPath + ".tmp";

            // Copy to temp location first for atomic install
            _fileSystem.FileCopy(extracted, tempTargetPath, overwrite: true);

            if (OperatingSystem.IsWindows() && _fileSystem.FileExists(targetPath))
            {
                // Windows rename trick: companion may be running (e.g. twig-mcp as MCP server)
                var oldPath = targetPath + ".old";
                try { _fileSystem.FileMove(targetPath, oldPath, overwrite: true); } catch { }
            }

            _fileSystem.FileMove(tempTargetPath, targetPath, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                _fileSystem.SetUnixFileMode(targetPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            results.Add(new CompanionUpdateResult(companionExe, Found: true, InstalledPath: targetPath));
        }

        return results;
    }

    private void CleanupTempFiles(string tempArchive, string tempExtractDir)
    {
        try { _fileSystem.FileDelete(tempArchive); } catch (Exception) { }
        try { _fileSystem.DeleteDirectory(tempExtractDir, recursive: true); } catch (Exception) { }
    }

    private void ExtractTarGz(string archivePath, string extractDir)
    {
        using var fileStream = _fileSystem.FileOpenRead(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        ExtractTar(gzipStream, extractDir, _fileSystem);
    }

    /// <summary>
    /// Minimal tar reader — enough for single-binary archives.
    /// TAR format: 512-byte header blocks followed by data blocks (padded to 512).
    /// Validates that extracted entries do not escape <paramref name="extractDir"/>.
    /// </summary>
    internal static void ExtractTar(Stream tarStream, string extractDir, IFileSystem fileSystem)
    {
        var buffer = new byte[512];
        while (true)
        {
            var bytesRead = ReadExact(tarStream, buffer, 512);
            if (bytesRead < 512) break;

            // All-zero header signals end of archive
            if (IsAllZero(buffer)) break;

            // File name: bytes 0–99 (null-terminated)
            var name = ExtractString(buffer, 0, 100).Trim();
            if (string.IsNullOrEmpty(name)) break;

            // File size: bytes 124–135 (octal, null-terminated)
            var sizeStr = ExtractString(buffer, 124, 12).Trim();
            var size = Convert.ToInt64(sizeStr, 8);

            // Type flag: byte 156 ('0' or '\0' = regular file, '5' = directory)
            var typeFlag = (char)buffer[156];

            var safeName = name.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var outputPath = Path.Combine(extractDir, safeName);
            var fullOutput = Path.GetFullPath(outputPath);
            var fullBase = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
            if (!fullOutput.StartsWith(fullBase, StringComparison.Ordinal))
                throw new InvalidOperationException($"Path traversal detected in archive entry: {name}");

            if (typeFlag == '5')
            {
                fileSystem.CreateDirectory(outputPath);
            }
            else if (typeFlag is '0' or '\0')
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (dir is not null) fileSystem.CreateDirectory(dir);

                using var outFile = fileSystem.FileCreate(outputPath);
                var remaining = size;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, buffer.Length);
                    var read = ReadExact(tarStream, buffer, toRead);
                    if (read == 0) break;
                    outFile.Write(buffer, 0, read);
                    remaining -= read;
                }

                // Skip padding to 512-byte boundary
                var pad = (int)(512 - (size % 512)) % 512;
                if (pad > 0) ReadExact(tarStream, new byte[pad], pad);
            }
            else
            {
                // Skip non-regular entries
                var dataBlocks = (size + 511) / 512 * 512;
                var skipBuf = new byte[512];
                var remaining = dataBlocks;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, skipBuf.Length);
                    var read = ReadExact(tarStream, skipBuf, toRead);
                    if (read == 0) break;
                    remaining -= read;
                }
            }
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private static bool IsAllZero(byte[] buffer)
    {
        foreach (var b in buffer)
            if (b != 0) return false;
        return true;
    }

    private static string ExtractString(byte[] buffer, int offset, int length)
    {
        var end = offset;
        while (end < offset + length && buffer[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset);
    }
}
