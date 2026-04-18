# Companion Auto-Upgrade Plan

**Work Item:** #1643 — Auto-upgrade twig-mcp and twig-tui alongside twig upgrade  
**Type:** Issue  
> **Status**: ✅ Done

---

## Executive Summary

This plan bundles companion tools (`twig-mcp`, `twig-tui`) into the existing `twig-{rid}` release archive so that `twig upgrade`, installer scripts, and a synchronous first-run check all install and maintain companions automatically — preserving version synchronization via MinVer git tags, minimizing network overhead to a single download, and reusing the `SelfUpdater` extraction pipeline via an `ICompanionInstaller` interface. Platform-detection helpers (`DetectRid`, `FindAsset`) are extracted to a shared `PlatformHelper` in `Twig.Infrastructure` to resolve the cross-project dependency. twig-tui is included on a best-effort basis (see [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) policy).

---

## Background

### Current Architecture

The self-update system consists of three layers:

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `SelfUpdateCommand` | `src/Twig/Commands/SelfUpdateCommand.cs` | Orchestrates the upgrade flow: version check → asset detection → download → apply |
| `SelfUpdater` | `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | Downloads archive, extracts **one** binary (`twig.exe`/`twig`), replaces current executable |
| `IGitHubReleaseService` / `GitHubReleaseClient` | `src/Twig.Infrastructure/GitHub/` | Queries GitHub Releases API for latest release and asset metadata |

Supporting abstractions:
- `IHttpDownloader` — testable HTTP download abstraction
- `IFileSystem` — testable file/directory operations (provides `FileOpenRead`/`FileCreate` for stream-based I/O; no `ReadAllText`/`WriteAllText`)
- `SemVerComparer` — AOT-safe version comparison
- `BinaryLauncher` — locates and launches companion binaries (adjacent directory → PATH fallback)

### Companion Tools

| Tool | Project | Assembly Name | AOT | Publish Strategy | Notes |
|------|---------|---------------|-----|------------------|-------|
| twig-mcp | `src/Twig.Mcp/Twig.Mcp.csproj` | `twig-mcp` | ✅ Yes (`PublishAot=true` in csproj) | Native AOT, self-contained (~15 MB) | MCP server, stdio transport |
| twig-tui | `src/Twig.Tui/Twig.Tui.csproj` | `twig-tui` | ❌ No (`IsAotCompatible=false`) | `PublishSingleFile=true`, self-contained, **no trimming** (~30–40 MB) | Terminal.Gui v2 beta uses reflection extensively; not AOT-compatible; trimming produces runtime failures |

Both companions:
- Share the same `Directory.Build.props` (MinVer, .NET 10, `TreatWarningsAsErrors`)
- Reference `Twig.Domain` and `Twig.Infrastructure`
- Are expected to live in the same directory as the main `twig` binary (`~/.twig/bin/`)

**Note**: `PublishAot` is set **per-project** in individual csproj files (e.g., `Twig.csproj`, `Twig.Mcp.csproj`), not in the shared `Directory.Build.props`.

### Current Release Pipeline

The release workflow (`.github/workflows/release.yml`) builds and packages **only** the main
`twig` binary:

```yaml
- run: dotnet publish src/Twig/Twig.csproj -c Release -r ${{ matrix.rid }} --self-contained true -o ./publish/${{ matrix.rid }}
```

Release assets: `twig-win-x64.zip`, `twig-linux-x64.tar.gz`, `twig-osx-x64.tar.gz`, `twig-osx-arm64.tar.gz`.

Companion binaries are **not** included in release archives.

### Local Development

`publish-local.ps1` already publishes both `twig` and `twig-mcp` to `~/.twig/bin/`, but does **not** publish `twig-tui`.

### GitHub Release API — Version Matching

`GitHubReleaseClient.GetLatestReleaseAsync()` fetches the **latest** release tag, not a
release matching a specific version. During first-run companion install, this means:
- If a user is on `v1.2` but latest is `v1.3`, companion download would fetch `v1.3` binaries.
- **Mitigation**: Add a `GetReleaseByTagAsync(string tag)` method to `IGitHubReleaseService`
  so the first-run check can fetch the release matching `VersionHelper.GetVersion()`.
  `SelfUpdateCommand` continues using `GetLatestReleaseAsync()` since it is already upgrading
  to the latest.

### Call-Site Audit

| File | Method/Location | Current Usage | Impact |
|------|----------------|---------------|--------|
| `SelfUpdateCommand.cs` | `ExecuteAsync()` (returns `Task<int>`) | Calls `selfUpdater.UpdateBinaryAsync()` (returns `Task<string>`); awaited result assigned to `newPath` local variable | Must handle new `UpdateResult` return type from `UpdateBinaryAsync`; destructure to get companion results |
| `SelfUpdateCommand.cs` | `FindAsset()` | Looks for `twig-{rid}` asset only | Delete; update all callers and tests to use `PlatformHelper.FindAsset()` directly |
| `SelfUpdateCommand.cs` | `DetectRid()` | Detects platform RID | Delete; update all callers and tests to use `PlatformHelper.DetectRid()` directly |
| `SelfUpdater.cs` | `UpdateBinaryAsync()` | Extracts ONLY `twig.exe`/`twig` from archive | Must also extract companion binaries; new `InstallCompanionsOnlyAsync` method for first-run check; implement `ICompanionInstaller` |
| `SelfUpdater.cs` | `CleanupOldBinary()` | Cleans `twig.exe.old` only | Must also clean companion `.old` files |
| `Program.cs:27` | Top-level | Calls `SelfUpdater.CleanupOldBinary()` on every startup | Add first-run companion check nearby |
| `Program.cs:818` | `Tui()` | `BinaryLauncher.Launch("twig-tui", ...)` | No change (already handles missing binary) |
| `Program.cs:824` | `Mcp()` | `BinaryLauncher.Launch("twig-mcp", ...)` | No change |
| `CommandRegistrationModule.cs` | `AddSelfUpdateCommands()` | Resolves `repoSlug` from `AssemblyMetadataAttribute("GitHubRepo")` via `typeof(TwigCommands).Assembly`; registers `GitHubReleaseClient` and `SelfUpdater` | No change — `Program.cs` reads the same attribute inline |
| `install.ps1` | Script body | Downloads and extracts `twig-win-x64.zip`, verifies `twig.exe` | Verify companion binaries too |
| `install.sh` | Script body | Downloads and extracts `twig-{rid}.tar.gz`, verifies `twig` | Verify and `chmod +x` companions too |
| `publish-local.ps1` | `Invoke-Publish` calls | Publishes `twig` + `twig-mcp` (label says "AOT, win-x64") | Add `twig-tui` |
| `release.yml` | Build job | Only `dotnet publish src/Twig/Twig.csproj` | Add companion publish steps with per-project flags |
| `SelfUpdaterTests.cs` | Multiple test methods | Tests single-binary extraction; asserts `result.ShouldBe(currentExe)` (string) | Add companion extraction tests; update assertions for `UpdateResult` type |
| `SelfUpdateCommandTests.cs` | `ExecuteAsync` tests + `StubReleaseService` | Tests single-binary upgrade flow; `StubReleaseService` implements `IGitHubReleaseService` with 2 methods | Add companion upgrade tests; add `GetReleaseByTagAsync` to `StubReleaseService` |
| `ChangelogCommandTests.cs` | `StubReleaseService` | Separate `StubReleaseService` implementing `IGitHubReleaseService` with 2 methods | Add `GetReleaseByTagAsync` to this stub too (interface compliance) |

---

## Problem Statement

The `twig upgrade` command only updates the main `twig` binary, leaving companion tools
(`twig-mcp`, `twig-tui`) at stale versions. Users who rely on these companions must manually
rebuild or re-install them after every upgrade. Additionally:

1. **Version skew**: After `twig upgrade`, `twig-mcp` may be running a different version than
   `twig`, sharing `Twig.Domain` and `Twig.Infrastructure` code that may have changed. This can
   cause subtle bugs or crashes.
2. **Missing companions on fresh install**: The installer scripts (`install.ps1`, `install.sh`)
   only provision `twig`. Users must separately discover and install companions.
3. **No first-run recovery**: If a user upgrades `twig` via a mechanism that doesn't include
   companions (e.g., manual binary replacement), there is no detection or recovery path.

---

## Goals and Non-Goals

### Goals

1. `twig upgrade` detects installed companion tools and upgrades them atomically with the main
   binary (single archive download, all-or-nothing extraction).
2. `twig upgrade` installs companions that are missing (even if the main binary is already up
   to date), providing a recovery path for incomplete installations.
3. Installer scripts (`install.ps1`, `install.sh`) provision `twig-mcp` and `twig-tui` by
   default alongside `twig` (subject to the [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) policy).
4. A synchronous first-run check detects missing companions after a version change and installs
   them automatically (with a timeout to bound startup delay).
5. All three binaries remain version-synchronized via MinVer git tags.
6. First-run companion install uses tag-matched release lookup (not latest) to prevent version
   skew between the main binary and companions.

### Non-Goals

- **Selective companion installation**: Users cannot opt out of individual companions. All
  available companions are installed. (If size becomes a concern, this can be revisited.)
- **Companion-specific upgrade**: There is no `twig upgrade --mcp-only` command. Companions
  are always upgraded alongside the main binary.
- **twig-tui AOT or trimming**: Terminal.Gui v2 does not support AOT (`IsAotCompatible=false`
  in csproj) and relies on reflection. Publish with `PublishSingleFile=true, SelfContained=true,
  PublishTrimmed=false`. See [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion)
  for the validation gate and fallback policy.
- **Rollback of individual companions**: The existing `publish-local.ps1 -Restore` backup/restore
  mechanism operates on the entire `~/.twig/bin/` directory, not individual binaries.

---

## Requirements

### Functional

| ID | Requirement |
|----|-------------|
| F1 | `twig upgrade` downloads a single archive containing all three binaries and extracts them to the install directory |
| F2 | `twig upgrade` reports which companions were upgraded or installed in the console output |
| F3 | `twig upgrade` upgrades companions even when the main binary is already at the latest version |
| F4 | On startup, `twig` detects a version change and checks for missing companions |
| F5 | Missing companions discovered at startup are downloaded and installed synchronously using a tag-matched release (not latest), bounded by a timeout |
| F6 | `install.ps1` extracts and verifies all three binaries from the release archive |
| F7 | `install.sh` extracts, verifies, and `chmod +x` all three binaries from the release archive |
| F8 | The release pipeline builds and bundles companion binaries into the existing per-platform archives |

### Non-Functional

| ID | Requirement |
|----|-------------|
| NF1 | First-run companion check must not add measurable latency when companions are present (file-existence check only; no network call) |
| NF2 | Network failures during first-run companion installation must not affect command execution or return codes |
| NF3 | All new code must be AOT-compatible and pass `TreatWarningsAsErrors` |
| NF4 | No telemetry changes — companion binary names are safe to log but companion paths/versions must not be sent |
| NF5 | Companion binaries must be written atomically (download to temp file, then move to target) to prevent corrupted binaries on interrupted downloads |
| NF6 | Failed companion installations must emit a diagnostic warning to stderr with a manual recovery path (`twig upgrade`) |

---

## Proposed Design

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                     twig upgrade flow                            │
│                                                                  │
│  SelfUpdateCommand                                               │
│  ┌─────────────────────────────────────────────────────────┐     │
│  │ 1. Check latest release (IGitHubReleaseService)         │     │
│  │ 2. Compare version (SemVerComparer)                     │     │
│  │ 3. Find asset for platform (FindAsset)                  │     │
│  │ 4. Download + extract archive (SelfUpdater)             │     │
│  │    ├── Update main binary (rename trick / overwrite)    │     │
│  │    └── Update companions (direct overwrite)       [NEW] │     │
│  │ 5. Report results                                 [NEW] │     │
│  └─────────────────────────────────────────────────────────┘     │
│                                                                  │
│  First-Run Check (Program.cs startup)                      [NEW] │
│  ┌─────────────────────────────────────────────────────────┐     │
│  │ 1. Check companion file existence (always, ~1ms)        │     │
│  │ 2. If all present → return immediately (no I/O write)   │     │
│  │ 3. If missing + marker matches → skip (already tried)   │     │
│  │ 4. If missing + version changed → synchronous download  │     │
│  │    └── Uses SelfUpdater.InstallCompanionsOnlyAsync()     │     │
│  │ 5. Write version marker after attempt                   │     │
│  └─────────────────────────────────────────────────────────┘     │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                   Release Pipeline                               │
│                                                                  │
│  release.yml build job:                                          │
│  ┌─────────────────────────────────────────────────────────┐     │
│  │ dotnet publish src/Twig/Twig.csproj     → twig(.exe)    │     │
│  │   (AOT, self-contained — PublishAot in csproj)          │     │
│  │ dotnet publish src/Twig.Mcp/...         → twig-mcp(.exe)│     │
│  │   (AOT, self-contained — PublishAot in csproj)          │     │
│  │ dotnet publish src/Twig.Tui/...         → twig-tui(.exe)│     │
│  │   (SingleFile, self-contained, NO trim, NO AOT)         │     │
│  │ Archive all three into twig-{rid}.zip/.tar.gz           │     │
│  └─────────────────────────────────────────────────────────┘     │
└──────────────────────────────────────────────────────────────────┘
```

### Key Components

#### 1. CompanionTool Registry (`CompanionTool.cs`)

A new file in `Twig.Infrastructure/GitHub/` that defines known companion tools:

```csharp
internal static class CompanionTools
{
    internal static readonly string[] All = ["twig-mcp", "twig-tui"];

    internal static string GetExeName(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;
}
```

This centralizes companion definitions so `SelfUpdater`, `SelfUpdateCommand`, and the
first-run check all reference the same source of truth.

Also in this file: `UpdateResult` and `CompanionUpdateResult` record types for the
`SelfUpdater` return value:

```csharp
public sealed record UpdateResult(
    string MainBinaryPath,
    IReadOnlyList<CompanionUpdateResult> Companions);

public sealed record CompanionUpdateResult(string Name, bool Found, string? InstalledPath);
```

**Note — TwigJsonContext not required:** `UpdateResult` and `CompanionUpdateResult` are
in-process return types consumed by `SelfUpdateCommand` and `CompanionFirstRunCheck`. They
are never serialized to JSON, never appear in HTTP responses, and never cross process
boundaries. They do **not** need `[JsonSerializable]` registration in `TwigJsonContext`.

#### 2. Repo Slug Resolution

The `repoSlug` is resolved from `AssemblyMetadataAttribute("GitHubRepo")`. Both callers
(`CommandRegistrationModule.AddSelfUpdateCommands()` and the `Program.cs` first-run check)
are in the CLI project and read the attribute inline — no shared method extraction needed.

#### 3. IGitHubReleaseService — Tag-Matched Lookup

Add `GetReleaseByTagAsync` to `IGitHubReleaseService` to prevent version skew during
first-run companion install. This method returns the same `GitHubReleaseInfo` DTO used by
`GetLatestReleaseAsync` — no new DTOs are needed; the GitHub API returns the same release
JSON structure for both `/releases/latest` and `/releases/tags/{tag}` endpoints:

```csharp
// Added to IGitHubReleaseService
Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string tag, CancellationToken ct = default);

// Implementation in GitHubReleaseClient
public async Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string tag, CancellationToken ct = default)
{
    var url = $"https://api.github.com/repos/{_repoSlug}/releases/tags/{tag}";
    using var request = CreateRequest(url);
    using var response = await _http.SendAsync(request, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        return null;
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    var dto = JsonSerializer.Deserialize(json, TwigJsonContext.Default.GitHubRelease);
    return dto is null ? null : MapToInfo(dto);
}
```

This ensures the first-run check downloads companions matching the *installed* twig version,
not the latest release.

#### 4. SelfUpdater Enhancement

The `UpdateBinaryAsync` method is modified to accept additional binary names and extract
them alongside the main binary. A new `InstallCompanionsOnlyAsync` method is added for
first-run check use, reusing the same download-extract pipeline to avoid code duplication.

**Shared extraction pipeline** — the existing download, archive extraction, binary search,
and installation logic is refactored into private helpers that both methods use:

```csharp
// Private shared helpers:
private async Task<string> DownloadArchiveAsync(string downloadUrl, string archiveName, CancellationToken ct)
    // Downloads archive to temp file, returns path

private string ExtractArchive(string tempArchive, string archiveName)
    // Extracts archive to temp dir, returns path to temp extract dir

private string? FindBinary(string directory, string binaryName)
    // Searches recursively for a named binary in the extracted archive (promoted from private to shared helper)

private void InstallBinaryToDir(string extractedBinary, string targetPath)
    // Handles atomic write: temp → move, Windows rename trick, Unix chmod
```

**`UpdateBinaryAsync` signature:**

`SelfUpdateCommand` and `SelfUpdater` are updated together in a single PR, so no
backward-compatible wrapper is needed. The old `Task<string>` overload is replaced
directly:

```csharp
// Replaces the old Task<string> overload directly
public async Task<UpdateResult> UpdateBinaryAsync(
    string downloadUrl, string archiveName,
    IReadOnlyList<string>? companionExeNames,
    CancellationToken ct = default)
```

**New method for first-run check:**
```csharp
/// <summary>
/// Downloads the archive and installs only companion binaries (not the main binary).
/// Used by CompanionFirstRunCheck to avoid duplicating the extraction pipeline.
/// </summary>
public async Task<IReadOnlyList<CompanionUpdateResult>> InstallCompanionsOnlyAsync(
    string downloadUrl, string archiveName,
    IReadOnlyList<string> companionExeNames,
    string installDir,
    CancellationToken ct = default)
```

This method reuses the same `DownloadArchiveAsync`, `ExtractArchive`, `FindBinary`, and
`InstallBinaryToDir` helpers as `UpdateBinaryAsync`, ensuring path traversal protection,
atomic writes, and cleanup are shared — not reimplemented.

**Companion extraction logic** (shared between both methods):
```
for each companionExeName in companionExeNames:
    find companion binary in extracted archive (FindBinary)
    if found:
        targetPath = installDir / companionExeName
        tempTargetPath = installDir / companionExeName + ".tmp"  [NF5: atomic write]
        copy extracted companion → tempTargetPath
        if Windows AND companion exists at targetPath:
            rename targetPath → targetPath.old  (may be running as MCP server)
        move tempTargetPath → targetPath  (atomic replace)
        if Unix: chmod +x targetPath
        record as Found=true
    else:
        record as Found=false (companion not in archive)
```

The Windows rename trick is applied to companions too because `twig-mcp` may be running
as an MCP server when the user invokes `twig upgrade`. The temp-file-then-move pattern
(NF5) prevents corrupted binaries if the download or extraction is interrupted.

**CleanupOldBinary enhancement:**
```csharp
public static void CleanupOldBinary()
{
    var fs = new DefaultFileSystem();
    var processPath = Environment.ProcessPath;
    CleanupOldBinaryCore(fs, processPath);
    // NEW: also clean companion .old files
    if (processPath is not null)
    {
        var dir = Path.GetDirectoryName(processPath);
        if (dir is not null)
        {
            foreach (var companion in CompanionTools.All)
            {
                var oldPath = Path.Combine(dir, CompanionTools.GetExeName(companion) + ".old");
                try { if (fs.FileExists(oldPath)) fs.FileDelete(oldPath); } catch { }
            }
        }
    }
}
```

**Testability — `ICompanionInstaller` interface:**

`SelfUpdater` is a `public sealed class`, which means NSubstitute cannot mock it directly.
To enable unit testing of `CompanionFirstRunCheck` in isolation, a narrow interface is
extracted for the companion-install capability:

```csharp
internal interface ICompanionInstaller
{
    Task<IReadOnlyList<CompanionUpdateResult>> InstallCompanionsOnlyAsync(
        string downloadUrl, string archiveName,
        IReadOnlyList<string> companionExeNames,
        string installDir,
        CancellationToken ct = default);
}
```

`SelfUpdater` implements `ICompanionInstaller`. `CompanionFirstRunCheck` depends on
`ICompanionInstaller` (not `SelfUpdater` directly), allowing tests to use
`NSubstitute.Substitute.For<ICompanionInstaller>()` for clean unit tests. This follows
the existing pattern of `IHttpDownloader` abstracting `HttpClient` for testability.

`SelfUpdater` itself is tested via its existing `internal` constructor that accepts
`IHttpDownloader`, `IFileSystem`, and `string? processPath` — the new companion
extraction methods use the same injected dependencies and are tested with the same
mock infrastructure.

#### 5. PlatformHelper — Shared RID Detection and Asset Lookup

`SelfUpdateCommand.DetectRid()` and `SelfUpdateCommand.FindAsset()` are currently
`internal static` methods in the CLI project (`Twig`). `CompanionFirstRunCheck` lives
in `Twig.Infrastructure` and needs these same helpers, but Infrastructure cannot
reference the CLI project (wrong dependency direction).

**Solution:** Extract both methods to a new `PlatformHelper` static class in
`Twig.Infrastructure/GitHub/PlatformHelper.cs`:

```csharp
internal static class PlatformHelper
{
    internal static string? DetectRid() { /* moved from SelfUpdateCommand */ }

    internal static (GitHubReleaseAssetInfo? asset, string archiveName) FindAsset(
        GitHubReleaseInfo release, string rid) { /* moved from SelfUpdateCommand */ }
}
```

`SelfUpdateCommand.DetectRid()` and `SelfUpdateCommand.FindAsset()` are **deleted**.
All callers (including existing tests) are updated to call `PlatformHelper` directly.
Tests are not public API — keeping delegating wrappers adds indirection with no benefit.

#### 6. SelfUpdateCommand Changes

The command is modified to:
1. Build a list of companion exe names to pass to `SelfUpdater`
2. Report companion results in console output
3. Detect and install companions even when the main binary is already current

**Key flow change — companion-only installation:**
When `comparison >= 0` (already up to date), the command currently returns 0 immediately.
The new behavior checks for missing companions and, if any are missing, downloads the
current release's archive to install them.

```
if already up to date:
    check if any companions are missing from install dir
    if all present → return 0 (truly up to date)
    else → download current release archive, extract missing companions
```

#### 7. First-Run Companion Check (`CompanionFirstRunCheck`)

A **non-static** class in `Twig.Infrastructure/GitHub/` with injected dependencies.
Dependencies are injected via interfaces (`IGitHubReleaseService`, `ICompanionInstaller`,
`IFileSystem`) to enable isolated unit testing with NSubstitute:

```csharp
internal sealed class CompanionFirstRunCheck(
    IGitHubReleaseService releaseService,
    ICompanionInstaller companionInstaller,
    IFileSystem fileSystem)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    internal async Task EnsureCompanionsAsync(
        string? processPath, string currentVersion, CancellationToken ct = default)
    {
        if (processPath is null) return;
        var dir = Path.GetDirectoryName(processPath);
        if (dir is null) return;

        var versionFile = Path.Combine(dir, ".twig-version");

        // Phase 1: Quick check — are all companions present? (~1ms, no I/O write)
        var missingCompanions = CompanionTools.All
            .Select(CompanionTools.GetExeName)
            .Where(exe => !fileSystem.FileExists(Path.Combine(dir, exe)))
            .ToList();

        if (missingCompanions.Count == 0)
            return;

        // Phase 2: Version check — avoid re-downloading same version
        if (fileSystem.FileExists(versionFile))
        {
            using var stream = fileSystem.FileOpenRead(versionFile);
            using var reader = new StreamReader(stream);
            var storedVersion = reader.ReadToEnd().Trim();
            if (storedVersion == currentVersion)
                return; // Already attempted this version — user must run 'twig upgrade'
        }

        // Phase 3: Synchronous download with timeout
        Console.Error.WriteLine("Installing companion tools...");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeout);

            var rid = PlatformHelper.DetectRid();
            if (rid is null) throw new InvalidOperationException("Cannot determine platform RID.");

            var release = await releaseService.GetReleaseByTagAsync($"v{currentVersion}", cts.Token);
            if (release is null) throw new InvalidOperationException($"No release found for tag v{currentVersion}.");

            var (asset, archiveName) = PlatformHelper.FindAsset(release, rid);
            if (asset is null) throw new InvalidOperationException($"No asset found for platform '{rid}'.");

            var results = await companionInstaller.InstallCompanionsOnlyAsync(
                asset.BrowserDownloadUrl, archiveName, missingCompanions, dir, cts.Token);

            var installed = results.Count(r => r.Found);
            Console.Error.WriteLine($"  Installed {installed} companion tool(s).");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("  Companion installation timed out. Run 'twig upgrade' to install manually.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Companion installation failed: {ex.Message}");
            Console.Error.WriteLine("  Run 'twig upgrade' to install manually.");
        }

        // Phase 4: Write marker after attempt (one attempt per version)
        WriteVersionMarker(fileSystem, versionFile, currentVersion);
    }

    private static void WriteVersionMarker(IFileSystem fs, string path, string version)
    {
        using var stream = fs.FileCreate(path);
        using var writer = new StreamWriter(stream);
        writer.Write(version);
    }
}
```

**Key design decisions for the first-run check:**

1. **Synchronous, not fire-and-forget**: The download is blocking (with a 60s timeout) to
   ensure it actually completes before the process exits. Typical twig commands complete in
   <1s, so a fire-and-forget `Task.Run` would be killed before the download finishes. The
   60s timeout accommodates the estimated 45–60 MB compressed archive on slow connections
   (rural broadband, throttled networks). The value is a `TimeSpan` constant — easy to tune
   in a follow-up if needed.

2. **Version marker written only after install attempt**: The marker records that an install
   was attempted at this version, preventing retries on every command. The fast path (all
   companions present) does *not* write the marker — a file write on every successful startup
   would violate NF1 and is unnecessary since the presence check already guards the download.
   If the download fails, the marker is still written — the user must run `twig upgrade`
   explicitly for recovery.

3. **Companion existence is checked first**: Even before reading the version marker, the
   check tests whether companion files exist. If all are present, no network call is made
   (NF1: ~1ms overhead). This also handles the case where a user manually installs companions.

4. **Reuses `SelfUpdater.InstallCompanionsOnlyAsync` via `ICompanionInstaller`**: No
   duplication of archive download, extraction, path traversal validation, `FindBinary`
   search, or atomic write logic. All security protections from `SelfUpdater` are inherited.
   The `ICompanionInstaller` interface enables NSubstitute mocking in unit tests.

5. **Non-static with injected dependencies**: `IGitHubReleaseService`, `ICompanionInstaller`,
   and `IFileSystem` are constructor-injected, enabling full unit test coverage with mocked
   dependencies. Tests can verify: version marker behavior, companion detection, download
   triggers, failure handling, and timeout scenarios — all without real HTTP or file I/O.

6. **Diagnostic output to stderr**: Failed installations emit a warning with the specific
   error and a recovery path (`twig upgrade`), satisfying NF6.

**IFileSystem — no new methods required**: Version marker reads use `FileOpenRead` +
`StreamReader.ReadToEnd()` and writes use `FileCreate` + `StreamWriter.Write()`, both of
which are existing `IFileSystem` methods. This avoids extending the interface surface.

### Data Flow

```
User runs "twig upgrade"
  │
  ├─ SelfUpdateCommand.ExecuteAsync()
  │   ├─ Check latest release → GitHubReleaseInfo
  │   ├─ Compare versions → needs upgrade?
  │   │   ├─ YES: find asset, download archive
  │   │   │   └─ SelfUpdater.UpdateBinaryAsync(url, archive, companionNames)
  │   │   │       ├─ Download archive to temp file
  │   │   │       ├─ Extract archive to temp directory
  │   │   │       ├─ Replace twig binary (rename trick / overwrite)
  │   │   │       ├─ Find and copy companion binaries (atomic: temp → move)
  │   │   │       ├─ Clean up temp files
  │   │   │       └─ Return UpdateResult
  │   │   └─ NO: check for missing companions
  │   │       ├─ All present → "Already up to date"
  │   │       └─ Missing → download archive via InstallCompanionsOnlyAsync
  │   └─ Print upgrade summary (main + companions)
  │
  └─ Exit

User runs any "twig" command (startup)
  │
  ├─ SelfUpdater.CleanupOldBinary() — clean .old files (main + companions)
  ├─ CompanionFirstRunCheck.EnsureCompanionsAsync() — [NEW]
  │   ├─ Check companion file existence (always, ~1ms)
  │   ├─ All present? → return immediately (no I/O write)
  │   ├─ Missing + marker matches current version? → skip (already tried)
  │   └─ Missing + version changed? → synchronous download (60s timeout)
  │       ├─ GetReleaseByTagAsync(v{currentVersion}) for version-matched companions
  │       ├─ ICompanionInstaller.InstallCompanionsOnlyAsync() (reuses extraction pipeline)
  │       ├─ Write marker after attempt
  │       └─ Failures → stderr warning + "run 'twig upgrade'" (NF2, NF6)
  └─ Continue to command execution
  
  NOTE: .GetAwaiter().GetResult() is safe here because it runs before
        ConsoleApp.Create() — no SynchronizationContext exists yet.
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Bundle vs. separate archives | **Bundle** all binaries in one archive | Version sync, single download, matches `publish-local.ps1` pattern |
| Companion list location | `CompanionTools` static class | `string[]` is sufficient; single-field record adds abstraction with no benefit |
| Return type change | `UpdateResult` record; old signature replaced directly | `SelfUpdater` and `SelfUpdateCommand` are updated in the same PR — no wrapper needed |
| First-run download strategy | **Synchronous** with 60s timeout | Fire-and-forget killed by process exit; synchronous ensures completion; 60s accommodates 45–60 MB archive on slow connections |
| Version marker timing | Written **only after** download attempt | Fast path writes nothing — per-startup I/O violates NF1 (see "First-Run Companion Check" section) |
| First-run deduplication | `ICompanionInstaller.InstallCompanionsOnlyAsync` | Shared pipeline; inherits path traversal + atomic write protections (see "SelfUpdater Enhancement" section) |
| First-run testability | Non-static class, constructor-injected deps | `ICompanionInstaller` enables NSubstitute mocking of sealed `SelfUpdater` |
| Repo slug resolution | Inline at each call site | Single 2-line attribute lookup; no method extraction needed |
| Windows rename for companions | Apply rename trick | `twig-mcp` may be running as MCP server during upgrade |
| twig-tui publishing | `PublishSingleFile=true`, no trim, no AOT | Terminal.Gui v2 uses reflection; attempt inclusion, exclude if CI fails |
| Version-matched downloads | `GetReleaseByTagAsync()` for first-run | Prevents version skew between main binary and companions (see "IGitHubReleaseService — Tag-Matched Lookup" section) |
| File I/O for version marker | Existing `IFileSystem.FileOpenRead`/`FileCreate` | Avoids extending `IFileSystem` interface |
| Platform helpers location | `PlatformHelper` in `Twig.Infrastructure/GitHub/` | Resolves cross-project dependency; `SelfUpdateCommand`'s methods deleted (see "PlatformHelper" section) |
| First-run HttpClient sharing | Single `HttpClient` via `NetworkServiceModule.CreateHttpClient()` | Avoids duplicate instances; shared gzip/Brotli/HTTP2 config |
| `.GetAwaiter().GetResult()` safety | Must run **before** `ConsoleApp.Create()` | No `SynchronizationContext` → blocking is safe; inline comment required |
| Shared helper promotion | `FindBinary` promoted to shared private helper | Used by both `UpdateBinaryAsync` and `InstallCompanionsOnlyAsync`; previously only called in single-binary extraction |
| Concurrent first-run safety | No locking — duplicate downloads are harmless | Two simultaneous `twig` commands may race on the version marker; worst case is a redundant download. Atomic writes prevent binary corruption. Both processes write the same marker value, so no inconsistency results. File locking would add complexity for a benign edge case. |
| `UpdateResult` serialization | **Not registered** in `TwigJsonContext` | In-process return types only — never serialized to JSON (see "CompanionTool Registry" note) |

#### Conditional twig-tui Inclusion

Per user decision (RQ-5): **"Try it. Omit TUI if there's an issue and we can follow up."**

twig-tui is published with `PublishSingleFile=true`, `SelfContained=true`, and
`PublishTrimmed=false`. Terminal.Gui v2 relies on reflection for property binding, view
construction, and event handling — trimming produces silent runtime failures.

**Validation gate:** T-1645-2 publishes twig-tui as SingleFile and runs a smoke test.
If the publish fails or the binary produces runtime errors, the following exclusion steps
are applied:

1. Remove `twig-tui` from the `CompanionTools.All` array
2. Remove the twig-tui `dotnet publish` step from `release.yml`
3. Remove twig-tui verification from `install.ps1` and `install.sh`
4. File a follow-up issue for twig-tui distribution

This is acceptable because twig-tui is the newest companion and its absence does not
block core twig or twig-mcp functionality. All subsequent sections reference this policy
rather than restating the conditional logic.

---

## Alternatives Considered

### Separate companion archives

Instead of bundling all binaries in one archive, each companion would have its own release
asset (e.g., `twig-mcp-win-x64.zip`). The upgrade command would download each separately.

**Pros:**
- Smaller download when only main binary needs updating
- Users could opt out of specific companions

**Cons:**
- Multiple HTTP requests per upgrade
- Version synchronization complexity (what if one download fails?)
- Significantly more code in `SelfUpdateCommand` and `release.yml`
- Doesn't match existing `publish-local.ps1` pattern

**Decision:** Rejected. The simplicity of single-archive bundling and guaranteed version
sync outweigh the marginal size savings.

### Fire-and-forget first-run download

Instead of blocking startup, fire-and-forget the download via `Task.Run`.

**Pros:**
- No startup delay
- Command execution unblocked

**Cons:**
- Process typically exits in <1s; the download task is killed before completing
- Writing version marker before download completion blocks retries
- Combined effect: first-run companion install is effectively non-functional

**Decision:** Rejected. Synchronous download with a 60s timeout is chosen instead.
The one-time startup delay is acceptable for reliable companion installation.

---

## Dependencies

### External
- **GitHub Releases API**: Used for both upgrade and first-run companion download.
  First-run check uses the `/releases/tags/{tag}` endpoint for version-matched lookup.
- **MinVer**: All three projects derive version from git tags (existing dependency)
- **Terminal.Gui v2**: Must support `PublishSingleFile` for `twig-tui` bundling. Resolved
  question: attempt inclusion, exclude if validation fails (see Resolved Questions). CI
  validation task T-1645-2 is the gate.

### Internal
- **`SelfUpdater`**: Must be extended (companion extraction + `InstallCompanionsOnlyAsync` + implement `ICompanionInstaller`)
  before `SelfUpdateCommand` and `CompanionFirstRunCheck` can use it
- **`ICompanionInstaller`**: Must be defined in `CompanionTool.cs` before `SelfUpdater` can implement it and `CompanionFirstRunCheck` can depend on it
- **`PlatformHelper`**: Must be extracted from `SelfUpdateCommand` before `CompanionFirstRunCheck` can call `DetectRid`/`FindAsset`
- **`IGitHubReleaseService`**: Must add `GetReleaseByTagAsync` before first-run check
- **`StubReleaseService`** (both in `SelfUpdateCommandTests` and `ChangelogCommandTests`): Each stub must add `GetReleaseByTagAsync` in place to satisfy the updated interface. The two stubs have different constructor shapes and are kept separate.
- **`release.yml`**: Must bundle companions before upgrade can install them (deploy dependency)
- **`CompanionTools` registry**: Must exist before any consumer can reference it
- **`InternalsVisibleTo` configuration** (load-bearing): `CompanionFirstRunCheck`, `ICompanionInstaller`,
  and `NetworkServiceModule.CreateHttpClient()` are all `internal` in `Twig.Infrastructure`. The CLI
  project (`Twig`) can access them because `Twig.Infrastructure.csproj` declares
  `InternalsVisibleTo` for assembly `Twig` (line 22–24 of the csproj). This existing configuration
  is required — do not remove or rename it. Test projects (`Twig.Infrastructure.Tests`, `Twig.Cli.Tests`)
  also have `InternalsVisibleTo` entries for the same reason.

### Sequencing
1. `CompanionTools` registry + `ICompanionInstaller` + `PlatformHelper` → `SelfUpdater` changes → `SelfUpdateCommand` changes → tests
2. `release.yml` changes can proceed in parallel with code changes
3. Installer script changes depend on `release.yml` (scripts extract what the pipeline bundles)
4. First-run check depends on `CompanionTools` registry, `ICompanionInstaller`, `PlatformHelper`, `GetReleaseByTagAsync`, and `SelfUpdater` implementing `ICompanionInstaller`

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `IGitHubReleaseService` / `GitHubReleaseClient` | Add `GetReleaseByTagAsync` method |
| `SelfUpdater` | Implement `ICompanionInstaller`; return type change, companion extraction, new `InstallCompanionsOnlyAsync`, shared extraction helpers |
| `SelfUpdateCommand` | Companion-aware upgrade flow, "install missing" when current; `DetectRid`/`FindAsset` delegate to `PlatformHelper` |
| `Program.cs` | First-run companion check added after existing cleanup; shared `HttpClient`; ordering constraint documented |
| `StubReleaseService` (2 instances) | Both stubs in test projects add `GetReleaseByTagAsync` implementation |
| `install.ps1` | Verification of companion binaries added |
| `install.sh` | Verification and `chmod +x` for companions added |
| `release.yml` | Build + bundle steps for twig-mcp and twig-tui added |
| `publish-local.ps1` | twig-tui publish step added; `Invoke-Publish` label updated |
| `Twig.Tui.csproj` | `PublishSingleFile=true`, `SelfContained=true`, `PublishTrimmed=false` |

### Backward Compatibility

- **`SelfUpdater.UpdateBinaryAsync`** — The old `Task<string>` overload is replaced directly
  with the new `Task<UpdateResult>` signature in PG-1. `SelfUpdater` and `SelfUpdateCommand`
  are updated in the same PR, so no backward-compatible wrapper is needed. No external
  consumers exist.
- **Release archives** will now contain additional files. Old versions of `twig upgrade` will
  extract the archive but only look for `twig.exe` — companions are ignored. No breaking change.
- **Installer scripts** will now verify more binaries. Old archives without companions will
  cause verification warnings (not errors).

### Performance

- **Upgrade**: Archive download is larger. twig-mcp adds ~15 MB (AOT binary); twig-tui adds
  ~30–40 MB (`PublishSingleFile`, self-contained, no trimming). Total archive size per
  platform is estimated at **45–60 MB compressed**. This is a single download compared to the
  current single-binary download. No maximum archive size acceptance criterion is imposed —
  if download times become unacceptable, splitting twig-tui to a separate optional archive
  is tracked as a potential follow-up (see Risks table).
- **Startup**: First-run check adds one `File.Exists()` call per companion + one file read
  for version marker. Negligible overhead (~1ms).
- **First-run companion install**: One-time synchronous blocking download (60s timeout),
  only runs once per version when companions are missing. Blocks command execution during
  the download but is bounded by the timeout. Subsequent runs with the same version skip
  the download entirely (version marker check).

---

## Security Considerations

This feature adds background network downloads of executable binaries to the install
directory. Security implications:

| Concern | Mitigation |
|---------|------------|
| **Binary integrity** | Downloads use HTTPS exclusively via the GitHub Releases API. GitHub's CDN serves assets over TLS with certificate validation enforced by `HttpClient`. No plaintext HTTP fallback exists. |
| **Binary authenticity** | Assets are implicitly trusted because they are served from the authenticated GitHub Releases API endpoint for the twig repository. No checksum/signature validation is currently performed — this matches the existing `twig upgrade` behavior. |
| **Man-in-the-middle** | TLS certificate validation in .NET's `HttpClient` prevents MITM attacks. No custom certificate handlers or `ServerCertificateCustomValidationCallback` overrides are used. |
| **Path traversal** | `SelfUpdater.ExtractTar` already validates that extracted entries do not escape the target directory. This protection applies equally to companion extraction. |
| **Temp file cleanup** | Atomic write pattern (temp → move) ensures partial downloads are never left as executable binaries in the install directory. Temp files are cleaned in `finally` blocks. |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| twig-tui `PublishSingleFile` incompatibility with Terminal.Gui | Medium | High | T-1645-2 CI validation gate. If it fails, apply exclusion steps per [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) policy. twig-mcp is unaffected. |
| twig-tui trim warnings at publish time | Medium | **High** | `PublishTrimmed=false` set explicitly in `Twig.Tui.csproj`. If any transitive dependency enables trimming, add `<TrimMode>partial</TrimMode>` override. CI must verify a clean publish with zero trim warnings. |
| Archive size increase (45–60 MB compressed) slows download | Medium | Low | twig-tui at ~30–40 MB dominates. Monitor download times. If unacceptable, consider splitting twig-tui to a separate optional archive in a follow-up. |
| `twig-mcp` locked by running MCP server during upgrade | Medium | Low | Windows rename trick handles this (same as main binary). User must restart MCP server. |
| First-run synchronous download adds startup delay | Low | Low | Only triggers once per version upgrade, only when companions are missing. 60s timeout bounds worst case. Stderr message informs user. Timeout is a `TimeSpan` constant for easy tuning. |
| Version marker file corruption | Low | Low | If unreadable, treat as version change and re-check companions. |
| Concurrent first-run race condition | Low | Low | Two simultaneous `twig` commands could race on the version marker file. Worst case is a duplicate companion download (idempotent — atomic writes prevent corruption). Both processes write the same marker value, so no inconsistency results. No mitigation needed beyond existing atomic write pattern. |
| Interrupted download leaves corrupted companion | Low | Medium | Atomic write pattern (NF5): download to temp file, move to target. Partial downloads never appear as the final binary. |

---

## Resolved Questions

Documented below for audit trail. All questions in this section have been resolved
and do not require further discussion.

| ID | Question | Resolution |
|----|----------|------------|
| RQ-1 | Does Terminal.Gui v2 support `PublishSingleFile=true` without trimming? | **Resolved (user decision)**: Attempt inclusion. T-1645-2 is the CI validation gate. See [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) for fallback steps. |
| RQ-2 | Should the first-run companion download show a progress indicator? | **Resolved (user decision)**: Yes. The revised design uses synchronous download with a one-line stderr message: "Installing companion tools..." followed by a result count or error message. This is appropriate because the download only triggers once per version upgrade when companions are missing. |
| RQ-3 | How is `SelfUpdater` mocked in `CompanionFirstRunCheck` tests given it is a sealed class? | **Resolved (v5)**: Extract a narrow `ICompanionInstaller` interface that `SelfUpdater` implements. `CompanionFirstRunCheck` depends on `ICompanionInstaller`, enabling NSubstitute mocking. This follows the existing `IHttpDownloader` pattern for testable HTTP abstraction. |
| RQ-4 | How do `DetectRid` and `FindAsset` become accessible from `Twig.Infrastructure`? | **Resolved (v6)**: Extract both methods from `SelfUpdateCommand` (CLI project) to a shared `PlatformHelper` static class in `Twig.Infrastructure/GitHub/`. `SelfUpdateCommand.DetectRid()` and `FindAsset()` are **deleted**; all callers (including tests) are updated to call `PlatformHelper` directly. Wrappers would add indirection with no benefit — tests are not public API. |
| RQ-5 | Should twig-tui be included or excluded from the companion bundle? | **Resolved (v6, user decision)**: "Try it. Omit TUI if there's an issue and we can follow up." See [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) for the full policy. |
| RQ-6 | Should the 60s first-run download timeout be configurable via env var? | **Resolved**: No. Compile-time `TimeSpan` constant is sufficient. Add `TWIG_DOWNLOAD_TIMEOUT` in a follow-up only if real-world feedback shows 60s is too short. |
| RQ-7 | Should `twig upgrade` display a progress bar for companion downloads? | **Resolved**: No. Single stderr "Downloading…" line is sufficient. Progress bars would add complexity to `SelfUpdater`. Revisit if user feedback demands it. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/GitHub/CompanionTool.cs` | `CompanionTools` static registry, `UpdateResult`/`CompanionUpdateResult` records, `ICompanionInstaller` interface |
| `src/Twig.Infrastructure/GitHub/PlatformHelper.cs` | `PlatformHelper` static class with `DetectRid()` and `FindAsset()` extracted from `SelfUpdateCommand` for cross-project reuse |
| `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | Non-static sealed class implementing synchronous first-run companion detection and download using injected `IGitHubReleaseService`, `ICompanionInstaller`, and `IFileSystem` |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | Unit tests for `CompanionTools` registry (`All`, `GetExeName`) and `PlatformHelper` (`DetectRid`, `FindAsset`) |


### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | New `UpdateBinaryAsync` overload returning `UpdateResult` (old `Task<string>` signature replaced directly — no wrapper); implement `ICompanionInstaller`; refactor extraction into shared helpers (including `FindBinary` promotion); new `InstallCompanionsOnlyAsync`; companion extraction with atomic writes; `CleanupOldBinary` cleans companion `.old` files |
| `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | Add `GetReleaseByTagAsync(string tag, CancellationToken)` method |
| `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | Implement `GetReleaseByTagAsync` using `/releases/tags/{tag}` endpoint |
| `src/Twig/Commands/SelfUpdateCommand.cs` | Build companion name list; handle `UpdateResult`; "install missing companions" path when already current; `DetectRid` and `FindAsset` **deleted** — call sites updated to use `PlatformHelper` directly |
| `src/Twig/Program.cs` | Add `CompanionFirstRunCheck.EnsureCompanionsAsync()` call after `SelfUpdater.CleanupOldBinary()` |
| `src/Twig.Tui/Twig.Tui.csproj` | Add `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`, `<PublishTrimmed>false</PublishTrimmed>` *(subject to [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) validation)* |
| `.github/workflows/release.yml` | Add `dotnet publish` steps for twig-mcp (AOT) and twig-tui (SingleFile, no trim); bundle into existing archive |
| `install.ps1` | Verify `twig-mcp.exe` and `twig-tui.exe` after extraction; print companion versions |
| `install.sh` | Verify `twig-mcp` and `twig-tui` after extraction; `chmod +x`; print companion versions |
| `publish-local.ps1` | Add `Invoke-Publish "twig-tui"` step |
| `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs` | Add tests for companion extraction from zip/tar.gz archives; add tests for `InstallCompanionsOnlyAsync`; update existing tests for `UpdateResult` return type |
| `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | Add tests for companion upgrade/install flow; update inline `StubReleaseService` to add `GetReleaseByTagAsync` |
| `tests/Twig.Cli.Tests/Commands/ChangelogCommandTests.cs` | Update inline `StubReleaseService` to add `GetReleaseByTagAsync` |

---

## ADO Work Item Structure

### Issue #1644: Update twig upgrade to detect and upgrade companion tools

**Goal:** Modify the `twig upgrade` command to detect installed companion tools and upgrade
them alongside the main twig binary, using the existing release archive (which will be
extended to include companion binaries).

**Prerequisites:** None (can start immediately; release.yml changes in #1645 are needed
for end-to-end validation but not for unit-testable code changes).

**Tasks:**

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| T-1644-1 | **Create CompanionTool registry, ICompanionInstaller, PlatformHelper, and shared helpers** | `src/Twig.Infrastructure/GitHub/CompanionTool.cs`, `src/Twig.Infrastructure/GitHub/PlatformHelper.cs` | S | F1, F4 (foundation) |

T-1644-1 sub-steps:
- Define `CompanionTools` static class: `string[] All = ["twig-mcp", "twig-tui"]` + `GetExeName(string)` helper
- Add `UpdateResult` and `CompanionUpdateResult` sealed record types
- Add `ICompanionInstaller` narrow interface for `InstallCompanionsOnlyAsync`
- Extract `DetectRid()` and `FindAsset()` from `SelfUpdateCommand` into `PlatformHelper`
- No `IFileSystem` changes — version marker I/O uses existing `FileOpenRead`/`FileCreate`
| T-1644-2 | **Add `GetReleaseByTagAsync` to release service** | `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs`, `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs`, `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs` | S | F5 |

T-1644-2 sub-steps:
- Add `GetReleaseByTagAsync(string tag, CancellationToken)` to `IGitHubReleaseService` interface
- Implement in `GitHubReleaseClient` using the `/releases/tags/{tag}` GitHub API endpoint (returns same `GitHubReleaseInfo` DTO — no new DTOs needed)
- Add unit test for the new method
- Add `GetReleaseByTagAsync` to each existing `StubReleaseService` in `SelfUpdateCommandTests.cs` and `ChangelogCommandTests.cs` in place
| T-1644-3 | **Extend SelfUpdater for companions** | `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | M | F1, NF5 |

T-1644-3 sub-steps:
- Refactor `UpdateBinaryAsync` to extract shared helpers (`DownloadArchiveAsync`, `ExtractArchive`, `InstallBinaryToDir`)
- Replace old `Task<string>` overload with new: `UpdateBinaryAsync(url, archive, companionExeNames, ct)` returning `UpdateResult`
- Implement `ICompanionInstaller` interface on `SelfUpdater`
- Add `InstallCompanionsOnlyAsync` method for first-run check (reuses shared helpers)
- Apply Windows rename trick for locked companions (`twig-mcp` may be running)
- Extend `CleanupOldBinary` to clean companion `.old` files
| T-1644-4 | **Update SelfUpdateCommand for companion-aware upgrades** | `src/Twig/Commands/SelfUpdateCommand.cs` | M | F1, F2, F3 |

T-1644-4 sub-steps:
- Build companion exe name list from `CompanionTools.All`
- Switch to new `UpdateBinaryAsync` overload
- Handle `UpdateResult` to report companion upgrade results
- Add "install missing companions" path when main binary is already current
- **Delete** `SelfUpdateCommand.DetectRid()` and `FindAsset()`; update call sites to `PlatformHelper`
| T-1644-5a | **Add unit tests for SelfUpdater companion extraction** | `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs`, `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs`, `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | M | Verifies F1, F5, NF5 |

T-1644-5a test scenarios:
- Extracts companions from zip archive
- Extracts companions from tar.gz archive
- Handles missing companions in archive (records `Found=false`)
- Uses atomic writes (temp → move)
- `CleanupOldBinary` cleans companion `.old` files
- `InstallCompanionsOnlyAsync` installs only companions (not main binary)
- `GetReleaseByTagAsync` returns correct release for a given tag
- `PlatformHelper.DetectRid` and `FindAsset` produce correct results
| T-1644-5b | **Add unit tests for SelfUpdateCommand companion flow** | `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs`, `tests/Twig.Cli.Tests/Commands/ChangelogCommandTests.cs` | M | Verifies F2, F3 |

T-1644-5b sub-steps:
- Add tests: command reports companion results; command installs missing companions when current version
- Verify all `SelfUpdateCommand` call sites use `PlatformHelper` directly (no delegating wrappers)
- Update each existing `StubReleaseService` in place to add `GetReleaseByTagAsync` (returns `null` by default)

**Acceptance Criteria:**
- [ ] `SelfUpdater.UpdateBinaryAsync` (new overload) extracts all companion binaries found in the archive using atomic writes
- [ ] Old `Task<string>` overload replaced directly — no temporary backward-compatible wrapper
- [ ] `SelfUpdater.InstallCompanionsOnlyAsync` downloads and installs only companion binaries
- [ ] `SelfUpdater.CleanupOldBinary` removes companion `.old` files
- [ ] `SelfUpdateCommand` reports which companions were upgraded or not found
- [ ] `twig upgrade` installs missing companions even when main binary is up to date
- [ ] `SelfUpdater` implements `ICompanionInstaller` interface
- [ ] `PlatformHelper.DetectRid()` and `PlatformHelper.FindAsset()` work correctly from `Twig.Infrastructure`
- [ ] `SelfUpdateCommand.DetectRid()` and `FindAsset()` are **deleted**; all call sites use `PlatformHelper` directly
- [ ] `GetReleaseByTagAsync` returns the release matching a specific tag (not latest)
- [ ] All existing `SelfUpdater` and `SelfUpdateCommand` tests still pass
- [ ] New tests cover companion extraction, missing companions, atomic writes, and cleanup scenarios

---

### Issue #1645: Update installer scripts to include twig-mcp and twig-tui by default

**Goal:** Update the release pipeline to build and bundle companion binaries in the release
archive, and update installer scripts to verify and report companion installation.

**Prerequisites:** None (can proceed in parallel with #1644; the release pipeline changes
are independently testable).

**Tasks:**

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| T-1645-1 | **Add companion publish steps to release.yml** | `.github/workflows/release.yml` | M | F8 |
| T-1645-2 | **Add PublishSingleFile to Twig.Tui.csproj and validate** | `src/Twig.Tui/Twig.Tui.csproj` | S | F8, NF3 |
| T-1645-3 | **Update install.ps1 to verify companions** | `install.ps1` | S | F6 |
| T-1645-4 | **Update install.sh to verify companions** | `install.sh` | S | F7 |
| T-1645-5 | **Update publish-local.ps1 to include twig-tui** | `publish-local.ps1` | S | F8 (local dev) |

T-1645-1 sub-steps:
- After publishing `src/Twig/Twig.csproj`, add `dotnet publish` for `src/Twig.Mcp/Twig.Mcp.csproj` (same AOT flags: `-c Release -r ${{ matrix.rid }} --self-contained true`)
- Add `dotnet publish` for `src/Twig.Tui/Twig.Tui.csproj` (non-AOT: `-c Release -r ${{ matrix.rid }} --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false /p:PublishAot=false`)
- Flags apply identically across all four platform RIDs (win-x64, linux-x64, osx-x64, osx-arm64) — no platform-specific flag variations needed
- All three publish to the same `./publish/${{ matrix.rid }}/` directory so the existing archive step bundles them together

T-1645-2 sub-steps:
- Add `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`, `<PublishTrimmed>false</PublishTrimmed>` to Twig.Tui.csproj
- Validate: (a) clean publish with zero trim warnings on win-x64, (b) smoke-test launch of the SingleFile binary
- If either fails, apply exclusion steps per [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) policy

T-1645-3 sub-steps:
- After extracting the archive, verify that `twig-mcp.exe` and `twig-tui.exe` exist in the install directory
- Use warnings (not errors) if companions are missing (older archives)

T-1645-4 sub-steps:
- After extracting the archive, verify that `twig-mcp` and `twig-tui` exist
- Run `chmod +x` on each companion
- Use warnings for missing companions

T-1645-5 sub-steps:
- Add `Invoke-Publish "twig-tui" "src\Twig.Tui\Twig.Tui.csproj"` call

**Acceptance Criteria:**
- [ ] `release.yml` builds and bundles twig, twig-mcp, and twig-tui for all four platform RIDs
- [ ] twig-tui publishes as a single-file self-contained binary with trimming disabled
- [ ] twig-tui publish step uses explicit non-AOT flags (`/p:PublishSingleFile=true /p:PublishTrimmed=false /p:PublishAot=false`) identically on all platforms (win-x64, linux-x64, osx-x64, osx-arm64)
- [ ] twig-tui publish produces zero trim warnings
- [ ] `install.ps1` verifies all three binaries
- [ ] `install.sh` verifies and `chmod +x` all three binaries
- [ ] `publish-local.ps1` publishes all three binaries to `~/.twig/bin/`
- [ ] Missing companions in older archives produce warnings, not errors
- [ ] If twig-tui `PublishSingleFile` validation fails, apply exclusion per [Conditional twig-tui Inclusion](#conditional-twig-tui-inclusion) policy

---

### Issue #1646: Add first-run install of missing companions on next twig upgrade

**Goal:** Implement a synchronous first-run check that detects missing companion binaries
after a version change and installs them automatically with a bounded timeout.

**Prerequisites:** #1644 (CompanionTool registry, `ICompanionInstaller`, `PlatformHelper`,
`GetReleaseByTagAsync`, and `SelfUpdater` implementing `ICompanionInstaller` must exist).

**Tasks:**

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| T-1646-1 | **Implement CompanionFirstRunCheck** | `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | M | F4, F5, NF1, NF2, NF5, NF6 |

T-1646-1 sub-steps:
- Sealed non-static class with constructor-injected `IGitHubReleaseService`, `ICompanionInstaller`, `IFileSystem`
- Method: `EnsureCompanionsAsync(string? processPath, string currentVersion, CancellationToken)`
- **Phase 1**: Check companion file existence (always, ~1ms); if all present → return immediately, no I/O write
- **Phase 2**: Read version marker; if matches current version → skip (already attempted)
- **Phase 3**: Synchronous download with 60s timeout via `ICompanionInstaller.InstallCompanionsOnlyAsync()`
- **Phase 4**: Write version marker **only after** download attempt
- Platform detection via `PlatformHelper.DetectRid()` / `FindAsset()`
- Marker I/O: `IFileSystem.FileOpenRead` + `StreamReader` / `IFileSystem.FileCreate` + `StreamWriter`
| T-1646-2 | **Integrate first-run check into Program.cs** | `src/Twig/Program.cs` | S | F4 |

T-1646-2 sub-steps:
- Construct `CompanionFirstRunCheck` with **manual dependency wiring** (pre-DI, before `ConsoleApp.Create()`):
  ```csharp
  // After SelfUpdater.CleanupOldBinary(), before ConsoleApp.Create():
  var httpClient = NetworkServiceModule.CreateHttpClient();
  var repoSlug = "PolyphonyRequiem/twig"; // same default as CommandRegistrationModule
  // Read AssemblyMetadataAttribute("GitHubRepo") — same inline pattern as AddSelfUpdateCommands()
  var attrs = typeof(TwigCommands).Assembly
      .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
  foreach (var attr in attrs) { /* resolve repoSlug */ }

  var releaseService = new GitHubReleaseClient(httpClient, repoSlug);
  var selfUpdater = new SelfUpdater(httpClient); // implements ICompanionInstaller
  var fileSystem = new DefaultFileSystem();
  var firstRunCheck = new CompanionFirstRunCheck(releaseService, selfUpdater, fileSystem);

  // Safe to block — no SynchronizationContext exists before ConsoleApp.Create()
  firstRunCheck.EnsureCompanionsAsync(Environment.ProcessPath, VersionHelper.GetVersion())
      .GetAwaiter().GetResult();
  ```
- The `repoSlug` resolution duplicates the logic in `CommandRegistrationModule.AddSelfUpdateCommands()`. This is intentional — extracting a shared method would require either (a) making `AddSelfUpdateCommands` return the slug or (b) adding a static helper, both of which add coupling for a 4-line lookup. The duplication is bounded and explicit.
- Add inline comment documenting the ordering constraint: `// Must run before ConsoleApp.Create() — no SynchronizationContext, blocking is safe`
| T-1646-3 | **Add unit tests for first-run check** | `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | M | NF1, NF2, NF6 |

T-1646-3 test scenarios:
- All companions present → returns immediately, no I/O writes, no network call
- Version marker missing → checks companions + triggers download
- Version marker matches current version → skip even if companions missing
- Version marker mismatch → checks companions + triggers download
- `ICompanionInstaller.InstallCompanionsOnlyAsync` called with correct companion names and install dir
- Download failure → stderr warning, marker still written, exit code unaffected
- Download timeout → stderr warning, marker written
- All three dependencies (`IGitHubReleaseService`, `ICompanionInstaller`, `IFileSystem`) mocked via NSubstitute — no real HTTP or file I/O

**Acceptance Criteria:**
- [ ] First-run check does not execute a network call or write any I/O when all companion binaries are present (fast path)
- [ ] First-run check uses `IFileSystem` for all file I/O (fully testable, no raw `File.*`)
- [ ] First-run companion download uses `ICompanionInstaller.InstallCompanionsOnlyAsync` (no extraction logic duplication)
- [ ] First-run companion download uses `GetReleaseByTagAsync` for version-matched lookup
- [ ] First-run companion download uses `PlatformHelper.DetectRid()` and `PlatformHelper.FindAsset()` (not `SelfUpdateCommand`)
- [ ] Program.cs creates a single shared `HttpClient` via `NetworkServiceModule.CreateHttpClient()` for both `GitHubReleaseClient` and `SelfUpdater`
- [ ] `.GetAwaiter().GetResult()` usage has inline comment documenting pre-`ConsoleApp.Create()` ordering constraint
- [ ] Version marker is written **only after** a download attempt — fast path (all present) performs no file writes
- [ ] Missing companions with matching version marker → no retry (user must run `twig upgrade`)
- [ ] Synchronous download bounded by 60s timeout
- [ ] Download failures emit stderr warning with "Run 'twig upgrade' to install manually." (NF6)
- [ ] Download failures do not affect command exit codes (NF2)
- [ ] All new code is AOT-compatible and passes `TreatWarningsAsErrors`
- [ ] `CompanionFirstRunCheck` is non-static with all dependencies constructor-injected (testable)

---

## PR Groups

### PG-1a: Foundation types and platform helpers (wide)

**Tasks:** T-1644-1, T-1644-2
**Issues:** #1644
**Classification:** Wide — many files, foundational type definitions and interface changes
**Estimated LoC:** ~250 (implementation + tests)
**Files:** ~8

| File | Type |
|------|------|
| `src/Twig.Infrastructure/GitHub/CompanionTool.cs` | New |
| `src/Twig.Infrastructure/GitHub/PlatformHelper.cs` | New |
| `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | Modified |
| `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | New |
| `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | Modified (StubReleaseService: add `GetReleaseByTagAsync`) |
| `tests/Twig.Cli.Tests/Commands/ChangelogCommandTests.cs` | Modified (StubReleaseService: add `GetReleaseByTagAsync`) |

**Rationale for splitting:** The foundation types (`CompanionTools`, `ICompanionInstaller`,
`PlatformHelper`, `UpdateResult`, `GetReleaseByTagAsync`) are stable, independently
testable, and have no behavioral coupling to `SelfUpdater` or `SelfUpdateCommand`. Landing
them first reduces the diff size and review burden of PG-1b.

**Successors:** PG-1b

---

### PG-1b: SelfUpdater companion extraction + command (deep)

**Tasks:** T-1644-3, T-1644-4, T-1644-5a, T-1644-5b
**Issues:** #1644
**Classification:** Deep — complex extraction logic, platform-specific behavior, command flow changes
**Estimated LoC:** ~600 (implementation + tests)
**Files:** ~5

| File | Type |
|------|------|
| `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | Modified |
| `src/Twig/Commands/SelfUpdateCommand.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs` | Modified |
| `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | Modified (add InstallCompanionsOnlyAsync tests) |

**Infrastructure and command land together:** `SelfUpdater` return type change and
`SelfUpdateCommand` consumption are coupled — the old `Task<string>` overload is replaced
directly with no backward-compatible wrapper.

**Predecessors:** PG-1a (requires `CompanionTools`, `ICompanionInstaller`, `PlatformHelper`, `GetReleaseByTagAsync`)
**Successors:** PG-3 (first-run check)

---

### PG-2: Release pipeline and installer scripts (wide)

**Tasks:** T-1645-1, T-1645-2, T-1645-3, T-1645-4, T-1645-5
**Issues:** #1645
**Classification:** Wide — many files, mechanical changes
**Estimated LoC:** ~200
**Files:** ~5

| File | Type |
|------|------|
| `.github/workflows/release.yml` | Modified |
| `src/Twig.Tui/Twig.Tui.csproj` | Modified |
| `install.ps1` | Modified |
| `install.sh` | Modified |
| `publish-local.ps1` | Modified |

**Predecessors:** None (parallel with PG-1a, PG-1b, and PG-3)
**Successors:** None

---

### PG-3: First-run companion check (deep)

**Tasks:** T-1646-1, T-1646-2, T-1646-3
**Issues:** #1646
**Classification:** Deep — few files, synchronous networking + platform concerns
**Estimated LoC:** ~350 (implementation + tests)
**Files:** ~3

| File | Type |
|------|------|
| `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | New |
| `src/Twig/Program.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | Modified (created in PG-1a; extended here with first-run check tests) |

**Predecessors:** PG-1b (requires `SelfUpdater` implementing `ICompanionInstaller`)

---

## Execution Order

```
PG-1a (Foundation types)
  └──→ PG-1b (SelfUpdater + Command)
         └──→ PG-3 (First-run check)
PG-2 (Pipeline + scripts) [parallel with all]
```

PG-3 depends on PG-1b (not PG-2). PG-3 unit tests are independently
testable against the infrastructure layer. Integration testing with the full pipeline
(PG-2) can be done after all PRs merge.

Total estimated LoC across all PR groups: **~1,400**

---

## References

- [GitHub Releases API — Get the latest release](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
- [GitHub Releases API — Get a release by tag name](https://docs.github.com/en/rest/releases/releases#get-a-release-by-tag-name)
- [Terminal.Gui v2 — GitHub repository](https://github.com/gui-cs/Terminal.Gui)
- [.NET PublishSingleFile documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
