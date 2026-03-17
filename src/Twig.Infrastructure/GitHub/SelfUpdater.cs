using System.IO.Compression;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Downloads and applies self-update binaries from GitHub Releases.
/// Handles platform-specific file-lock strategies (Windows rename trick vs. Unix direct overwrite).
/// </summary>
public sealed class SelfUpdater
{
    private readonly HttpClient _http;

    public SelfUpdater(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
    }

    /// <summary>
    /// Downloads the archive from <paramref name="downloadUrl"/>, extracts the binary,
    /// and replaces the current executable.
    /// </summary>
    /// <returns>The path to the new binary.</returns>
    public async Task<string> UpdateBinaryAsync(string downloadUrl, string archiveName, CancellationToken ct = default)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");
        var currentDir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Cannot determine current executable directory.");

        // Download archive to a temp file
        var tempArchive = Path.Combine(Path.GetTempPath(), $"twig-update-{Guid.NewGuid():N}{Path.GetExtension(archiveName)}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Add("User-Agent", "twig-cli");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = File.Create(tempArchive);
            await stream.CopyToAsync(fileStream, ct);
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
            Directory.CreateDirectory(tempExtractDir);

            if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(tempArchive, tempExtractDir);
            }
            else if (archiveName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarGz(tempArchive, tempExtractDir);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported archive format: {archiveName}");
            }

            var extractedBinary = FindBinary(tempExtractDir, exeName)
                ?? throw new InvalidOperationException($"Could not find '{exeName}' in downloaded archive.");

            // Apply update
            if (OperatingSystem.IsWindows())
            {
                // Windows file-lock strategy: rename running exe → .old, copy new exe
                var oldPath = currentExe + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(currentExe, oldPath);
                File.Copy(extractedBinary, currentExe, overwrite: true);
            }
            else
            {
                // Unix: direct overwrite (running binary is not locked)
                File.Copy(extractedBinary, currentExe, overwrite: true);

                // chmod +x
                File.SetUnixFileMode(currentExe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return currentExe;
        }
        finally
        {
            // Clean up temp files
            try { File.Delete(tempArchive); } catch { }
            try { Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Cleans up the <c>.old</c> binary left behind from a previous Windows update.
    /// Safe to call on any platform — no-ops if no old binary exists.
    /// </summary>
    public static void CleanupOldBinary()
    {
        var currentExe = Environment.ProcessPath;
        if (currentExe is null) return;

        var oldPath = currentExe + ".old";
        try
        {
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }
        catch
        {
            // Best-effort cleanup — ignore if the old binary is still locked.
        }
    }

    private static void ExtractTarGz(string archivePath, string extractDir)
    {
        using var fileStream = File.OpenRead(archivePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        ExtractTar(gzipStream, extractDir);
    }

    private static void ExtractTar(Stream tarStream, string extractDir)
    {
        // Minimal tar reader — enough for single-binary archives.
        // TAR format: 512-byte header blocks followed by data blocks (padded to 512).
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
                Directory.CreateDirectory(outputPath);
            }
            else if (typeFlag is '0' or '\0')
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (dir is not null) Directory.CreateDirectory(dir);

                using var outFile = File.Create(outputPath);
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

    private static string? FindBinary(string directory, string binaryName)
    {
        // Search recursively (archive may have a top-level folder)
        foreach (var file in Directory.EnumerateFiles(directory, binaryName, SearchOption.AllDirectories))
            return file;
        return null;
    }
}
