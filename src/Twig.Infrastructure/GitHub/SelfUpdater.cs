using System.IO.Compression;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Downloads and applies self-update binaries from GitHub Releases.
/// <para>
/// Every binary — main and companion alike, on every platform — is written to a sibling
/// <c>.tmp</c> file and then renamed into place. A running executable can never be
/// overwritten in place: Linux raises <c>ETXTBSY</c> ("Text file busy") on a write to a
/// file currently being executed, and Windows holds an exclusive image lock. <c>rename(2)</c>
/// works on both because the running process keeps the old inode / image alive; the sibling
/// <c>.tmp</c> keeps the rename on one filesystem, which matters because <c>rename(2)</c>
/// cannot cross mount points. Windows additionally renames the live target to <c>.old</c>
/// first, because it will not let a locked image be replaced by a rename.
/// </para>
/// <para>
/// Installs are staged-then-committed: all binaries are copied to their <c>.tmp</c> siblings
/// before any of them is renamed into place, so a failure during download/extract/copy leaves
/// every live binary untouched rather than producing a mixed-version install.
/// </para>
/// Also supports companion binary extraction via <see cref="InstallCompanionsOnlyAsync"/>.
/// </summary>
public sealed class SelfUpdater : ICompanionInstaller
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
    /// <param name="force">When true, terminates any process holding a target binary open
    /// before downloading. When false (default), throws <see cref="UpdateBlockedException"/>
    /// if any peer binary is locked so the caller can surface the offending PIDs.</param>
    /// <returns>An <see cref="UpdateResult"/> with the main binary path and per-companion status.</returns>
    /// <exception cref="UpdateBlockedException">Thrown when <paramref name="force"/> is false
    /// and one or more peer binaries (companions) are held open by another process.</exception>
    public async Task<UpdateResult> UpdateBinaryAsync(
        string downloadUrl,
        string archiveName,
        IReadOnlyList<string>? companionExeNames,
        CancellationToken ct = default,
        bool force = false)
    {
        var currentExe = _processPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");
        var currentDir = Path.GetDirectoryName(currentExe)
            ?? throw new InvalidOperationException("Cannot determine current executable directory.");

        // Probe peer binaries (companions) BEFORE downloading. The main exe is excluded from
        // the lock check because Windows lets us rename a running .exe via the .old trick;
        // companions don't have that luxury when held by long-lived MCP/TUI processes.
        EnsurePeersWritable(currentDir, companionExeNames, force);

        var tempArchive = await DownloadArchiveAsync(downloadUrl, archiveName, ct);
        var tempExtractDir = ExtractArchive(tempArchive, archiveName);
        try
        {
            // Install main binary
            var exeName = OperatingSystem.IsWindows() ? "twig.exe" : "twig";
            var extractedBinary = FindBinary(tempExtractDir, exeName)
                ?? throw new InvalidOperationException($"Could not find '{exeName}' in downloaded archive.");

            // Stage everything first, commit second: a failure while copying leaves every
            // live binary untouched rather than producing a mixed-version install.
            FileLockProbe.TryRemoveStaleTemp(currentExe);
            StageBinary(extractedBinary, currentExe);
            var staged = StageCompanions(tempExtractDir, companionExeNames, currentDir);

            CommitBinary(currentExe);
            var companions = CommitCompanions(staged);

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
    /// <param name="force">When true, terminates processes holding any target companion open
    /// before downloading. When false (default), throws <see cref="UpdateBlockedException"/>.</param>
    public async Task<IReadOnlyList<CompanionUpdateResult>> InstallCompanionsOnlyAsync(
        string archiveUrl,
        string archiveName,
        IReadOnlyList<string> companionExeNames,
        string installDir,
        CancellationToken ct = default,
        bool force = false)
    {
        EnsurePeersWritable(installDir, companionExeNames, force);
        var tempArchive = await DownloadArchiveAsync(archiveUrl, archiveName, ct);
        var tempExtractDir = ExtractArchive(tempArchive, archiveName);
        try
        {
            return CommitCompanions(StageCompanions(tempExtractDir, companionExeNames, installDir));
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
    /// Pass an empty list for <paramref name="companionNames"/> to clean only the main binary.
    /// </summary>
    internal static void CleanupOldBinaryCore(IFileSystem fileSystem, string? processPath, IReadOnlyList<string>? companionNames = null)
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

        if (companionNames is null or { Count: 0 }) return;

        var dir = Path.GetDirectoryName(processPath);
        if (dir is null) return;

        foreach (var companion in companionNames)
        {
            var companionOldPath = Path.Combine(dir, CompanionTools.GetExeName(companion) + ".old");
            try
            {
                if (fileSystem.FileExists(companionOldPath))
                    fileSystem.FileDelete(companionOldPath);
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

    /// <summary>
    /// Probes companion binaries for write-locks and either kills the holders (when
    /// <paramref name="force"/> is true) or throws <see cref="UpdateBlockedException"/>.
    /// Always opportunistically removes stale <c>.tmp</c> siblings so re-runs after a
    /// previous failed update don't accumulate junk.
    /// </summary>
    private static void EnsurePeersWritable(string installDir, IReadOnlyList<string>? companionExeNames, bool force)
    {
        if (companionExeNames is null or { Count: 0 }) return;

        var paths = companionExeNames
            .Select(name => Path.Combine(installDir, name))
            .ToArray();

        // Always clean stale .tmp leftovers from prior failed attempts.
        foreach (var p in paths) FileLockProbe.TryRemoveStaleTemp(p);

        var probes = FileLockProbe.ProbeAll(paths);
        var blocked = probes.Where(r => r.IsLocked).ToList();
        if (blocked.Count == 0) return;

        if (!force)
        {
            throw new UpdateBlockedException(blocked);
        }

        foreach (var entry in blocked)
        {
            FileLockProbe.KillHolders(entry.Path);
        }

        // Re-probe; if anything is still locked after kill attempts, give up cleanly.
        var residual = FileLockProbe.ProbeAll(paths).Where(r => r.IsLocked).ToList();
        if (residual.Count > 0)
        {
            throw new UpdateBlockedException(residual);
        }
    }

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

    /// <summary>
    /// A binary copied to its <c>.tmp</c> sibling and awaiting the rename that commits it.
    /// </summary>
    private readonly record struct StagedBinary(string TargetPath);

    /// <summary>
    /// Copies <paramref name="extractedBinary"/> to <paramref name="targetPath"/> + <c>.tmp</c>.
    /// Never writes to <paramref name="targetPath"/> itself — that file may be the executable
    /// this process is running from, and writing to it raises <c>ETXTBSY</c> on Linux.
    /// </summary>
    private void StageBinary(string extractedBinary, string targetPath)
    {
        _fileSystem.FileCopy(extractedBinary, targetPath + ".tmp", overwrite: true);
    }

    /// <summary>
    /// Renames the staged <c>.tmp</c> over <paramref name="targetPath"/> and restores the
    /// executable bit on Unix. On Windows the live target is moved aside to <c>.old</c> first,
    /// because a locked image cannot be replaced by a rename.
    /// </summary>
    private void CommitBinary(string targetPath)
    {
        var tempPath = targetPath + ".tmp";

        if (OperatingSystem.IsWindows() && _fileSystem.FileExists(targetPath))
        {
            // Windows rename trick: the running exe cannot be replaced, but it can be renamed.
            var oldPath = targetPath + ".old";
            _fileSystem.FileMove(targetPath, oldPath, overwrite: true);
        }

        _fileSystem.FileMove(tempPath, targetPath, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            // chmod +x — the archive's mode bits do not survive extraction.
            _fileSystem.SetUnixFileMode(targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Copies every companion found in <paramref name="extractDir"/> to its <c>.tmp</c> sibling
    /// in <paramref name="installDir"/>. Companions missing from the archive are reported with
    /// <c>Found: false</c> and carry no staged file.
    /// </summary>
    private List<(CompanionUpdateResult Result, StagedBinary? Staged)> StageCompanions(
        string extractDir,
        IReadOnlyList<string>? companionExeNames,
        string installDir)
    {
        var staged = new List<(CompanionUpdateResult, StagedBinary?)>();
        if (companionExeNames is null or { Count: 0 })
            return staged;

        foreach (var companionExe in companionExeNames)
        {
            var extracted = FindBinary(extractDir, companionExe);
            if (extracted is null)
            {
                staged.Add((new CompanionUpdateResult(companionExe, Found: false, InstalledPath: null), null));
                continue;
            }

            var targetPath = Path.Combine(installDir, companionExe);
            StageBinary(extracted, targetPath);
            staged.Add((
                new CompanionUpdateResult(companionExe, Found: true, InstalledPath: targetPath),
                new StagedBinary(targetPath)));
        }

        return staged;
    }

    /// <summary>
    /// Renames every staged companion into place. Called only after all staging has succeeded.
    /// </summary>
    private IReadOnlyList<CompanionUpdateResult> CommitCompanions(
        List<(CompanionUpdateResult Result, StagedBinary? Staged)> staged)
    {
        var results = new List<CompanionUpdateResult>(staged.Count);
        foreach (var (result, entry) in staged)
        {
            if (entry is { } binary)
                CommitBinary(binary.TargetPath);

            results.Add(result);
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

    private static bool IsAllZero(byte[] buffer) => Array.TrueForAll(buffer, static b => b == 0);

    private static string ExtractString(byte[] buffer, int offset, int length)
    {
        var end = offset;
        while (end < offset + length && buffer[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset);
    }
}
