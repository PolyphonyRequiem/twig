using System.IO.Compression;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Downloads and applies self-update binaries from GitHub Releases.
/// Handles platform-specific file-lock strategies (Windows rename trick vs. Unix direct overwrite).
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
    /// Downloads the archive from <paramref name="downloadUrl"/>, extracts the binary,
    /// and replaces the current executable.
    /// </summary>
    /// <returns>The path to the new binary.</returns>
    public async Task<string> UpdateBinaryAsync(string downloadUrl, string archiveName, CancellationToken ct = default)
    {
        var currentExe = _processPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");
        var currentDir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Cannot determine current executable directory.");

        // Download archive to a temp file
        var tempArchive = Path.Combine(Path.GetTempPath(), $"twig-update-{Guid.NewGuid():N}{Path.GetExtension(archiveName)}");
        try
        {
            await _downloader.DownloadFileAsync(downloadUrl, tempArchive, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to download update from {downloadUrl}: {ex.Message}", ex);
        }

        // Extract binary from archive
        var exeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"twig-update-{Guid.NewGuid():N}");
        try
        {
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

            var extractedBinary = _fileSystem.EnumerateFiles(tempExtractDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"Could not find '{exeName}' in downloaded archive.");

            // Apply update
            if (OperatingSystem.IsWindows())
            {
                // Windows file-lock strategy: rename running exe → .old, copy new exe
                var oldPath = currentExe + ".old";
                _fileSystem.FileMove(currentExe, oldPath, overwrite: true);
                _fileSystem.FileCopy(extractedBinary, currentExe, overwrite: true);
            }
            else
            {
                // Unix: direct overwrite (running binary is not locked)
                _fileSystem.FileCopy(extractedBinary, currentExe, overwrite: true);

                // chmod +x
                _fileSystem.SetUnixFileMode(currentExe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return currentExe;
        }
        finally
        {
            // Clean up temp files
            try { _fileSystem.FileDelete(tempArchive); } catch (Exception) { }
            try { _fileSystem.DeleteDirectory(tempExtractDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Cleans up the <c>.old</c> binary left behind from a previous Windows update.
    /// Safe to call on any platform — no-ops if no old binary exists.
    /// </summary>
    public static void CleanupOldBinary()
    {
        CleanupOldBinaryCore(new DefaultFileSystem(), Environment.ProcessPath);
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
