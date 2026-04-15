# Companion Auto-Upgrade Plan

**Work Item:** #1643 — Auto-upgrade twig-mcp and twig-tui alongside twig upgrade  
**Type:** Issue  
**Status:** Revised (v3)

---

## Executive Summary

When `twig upgrade` runs today, it only updates the main `twig` binary, leaving companion
tools (`twig-mcp`, `twig-tui`) at their old versions or missing entirely. This plan introduces
a **bundled archive** strategy: companion binaries are included in the same release archive
as the main binary (`twig-{rid}.zip` / `twig-{rid}.tar.gz`), and the upgrade command,
installer scripts, and a lightweight first-run check all extract and maintain these companions
automatically. The design preserves version synchronization (all three binaries share the
same MinVer-derived version from git tags) and minimizes network overhead (single download).

> **twig-tui inclusion**: Terminal.Gui v2 does not support AOT and uses reflection extensively.
> The plan attempts `PublishSingleFile=true` with trimming disabled. If CI validation (T-1645-2)
> reveals runtime failures, twig-tui is excluded from the bundle and tracked as a follow-up issue.
> This fallback does not block twig or twig-mcp companion upgrade functionality.

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
- `IFileSystem` — testable file/directory operations (does NOT currently include `ReadAllText`/`WriteAllText`; provides `FileOpenRead`/`FileCreate` for stream-based I/O)
- `SemVerComparer` — AOT-safe version comparison
- `BinaryLauncher` — locates and launches companion binaries (adjacent directory → PATH fallback)

### Companion Tools

| Tool | Project | Assembly Name | AOT | Publish Strategy | Notes |
|------|---------|---------------|-----|------------------|-------|
| twig-mcp | `src/Twig.Mcp/Twig.Mcp.csproj` | `twig-mcp` | ✅ Yes | Native AOT, self-contained (~15 MB) | MCP server, stdio transport |
| twig-tui | `src/Twig.Tui/Twig.Tui.csproj` | `twig-tui` | ❌ No (`IsAotCompatible=false`) | `PublishSingleFile=true`, self-contained, **no trimming** (~60–80 MB) | Terminal.Gui v2 beta uses reflection extensively; not AOT-compatible; trimming produces runtime failures |

Both companions:
- Share the same `Directory.Build.props` (MinVer, .NET 10, `TreatWarningsAsErrors`)
- Reference `Twig.Domain` and `Twig.Infrastructure`
- Are expected to live in the same directory as the main `twig` binary (`~/.twig/bin/`)

### Current Release Pipeline

The release workflow (`.github/workflows/release.yml`) builds and packages **only** the main
`twig` binary:

```yaml
- run: dotnet publish src/Twig/Twig.csproj -c Release -r ${{ matrix.rid }} --self-contained true -o ./publish/${{ matrix.rid }}
```

Release assets: `twig-win-x64.zip`, `twig-linux-x64.tar.gz`, `twig-osx-x64.tar.gz`, `twig-osx-arm64.tar.gz`.

Companion binaries are **not** included in release archives.

### Local Development

`publish-local.ps1` already publishes both `twig` and `twig-mcp` to `~/.twig/bin/`, but does
**not** publish `twig-tui`. The `Invoke-Publish` function label says "AOT, win-x64" which is
only accurate for `twig` and `twig-mcp`; adding `twig-tui` (non-AOT, `PublishSingleFile`)
requires updating the function label to reflect that publish strategy varies by project.

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
| `SelfUpdateCommand.cs` | `FindAsset()` | Looks for `twig-{rid}` asset only | No change needed (companions bundled in same archive) |
| `SelfUpdater.cs` | `UpdateBinaryAsync()` | Extracts ONLY `twig.exe`/`twig` from archive | Must also extract companion binaries |
| `SelfUpdater.cs` | `CleanupOldBinary()` | Cleans `twig.exe.old` only | Must also clean companion `.old` files |
| `Program.cs:27` | Top-level | Calls `SelfUpdater.CleanupOldBinary()` on every startup | Add first-run companion check nearby |
| `Program.cs:659` | `Tui()` | `BinaryLauncher.Launch("twig-tui", ...)` | No change (already handles missing binary) |
| `Program.cs:665` | `Mcp()` | `BinaryLauncher.Launch("twig-mcp", ...)` | No change |
| `CommandRegistrationModule.cs` | `AddSelfUpdateCommands()` | Resolves `repoSlug` from `AssemblyMetadataAttribute("GitHubRepo")` via reflection; registers `GitHubReleaseClient` and `SelfUpdater` | First-run check must share repo slug resolution (extract to shared helper or constant) |
| `install.ps1` | Script body | Downloads and extracts `twig-win-x64.zip`, verifies `twig.exe` | Verify companion binaries too |
| `install.sh` | Script body | Downloads and extracts `twig-{rid}.tar.gz`, verifies `twig` | Verify and `chmod +x` companions too |
| `publish-local.ps1` | `Invoke-Publish` calls | Publishes `twig` + `twig-mcp` (label says "AOT, win-x64") | Add `twig-tui`; update label to reflect mixed publish strategies |
| `release.yml` | Build job | Only `dotnet publish src/Twig/Twig.csproj` | Add companion publish steps with per-project flags |
| `SelfUpdaterTests.cs` | Multiple test methods | Tests single-binary extraction; asserts `result.ShouldBe(currentExe)` (string) | Add companion extraction tests; update assertions for `UpdateResult` type |
| `SelfUpdateCommandTests.cs` | `ExecuteAsync` tests | Tests single-binary upgrade flow | Add companion upgrade tests |

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
   default alongside `twig`. If twig-tui fails `PublishSingleFile` validation (T-1645-2),
   only `twig-mcp` is provisioned and twig-tui becomes a follow-up issue.
4. A lightweight first-run check detects missing companions after a version change and installs
   them automatically without user intervention.
5. All three binaries remain version-synchronized via MinVer git tags.
6. First-run companion install uses tag-matched release lookup (not latest) to prevent version
   skew between the main binary and companions.

### Non-Goals

- **Selective companion installation**: Users cannot opt out of individual companions. All
  available companions are installed. (If size becomes a concern, this can be revisited.)
- **Companion-specific upgrade**: There is no `twig upgrade --mcp-only` command. Companions
  are always upgraded alongside the main binary.
- **twig-tui AOT compilation**: Terminal.Gui v2 does not support AOT. The TUI companion will
  be published as a self-contained single-file app with **no trimming** (Terminal.Gui uses
  reflection extensively; trimming causes runtime failures). If `PublishSingleFile` validation
  fails (T-1645-2), twig-tui is excluded entirely and tracked as a follow-up.
- **twig-tui trimming**: Explicitly excluded. Terminal.Gui v2 relies on reflection for
  property binding, view construction, and event handling. Enabling trim would silently
  break core TUI functionality. Publish with `PublishSingleFile=true, SelfContained=true,
  PublishTrimmed=false`. Risk elevated to **High** — see Risks and Mitigations table.
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
| F5 | Missing companions discovered at startup are downloaded and installed automatically using a tag-matched release (not latest) |
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
│  │ 1. Read ~/.twig/bin/.twig-version marker (via IFileSystem)│   │
│  │ 2. If version changed → check companion binaries        │     │
│  │ 3. If missing → download from TAG-MATCHED release       │     │
│  │ 4. Write version marker (via IFileSystem)               │     │
│  └─────────────────────────────────────────────────────────┘     │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                   Release Pipeline                               │
│                                                                  │
│  release.yml build job:                                          │
│  ┌─────────────────────────────────────────────────────────┐     │
│  │ dotnet publish src/Twig/Twig.csproj     → twig(.exe)    │     │
│  │   (AOT, self-contained)                                 │     │
│  │ dotnet publish src/Twig.Mcp/...         → twig-mcp(.exe)│     │
│  │   (AOT, self-contained)                                 │     │
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
internal sealed record CompanionTool(string BinaryName)
{
    internal string GetExeName() =>
        OperatingSystem.IsWindows() ? $"{BinaryName}.exe" : BinaryName;
}

internal static class CompanionTools
{
    internal static readonly CompanionTool Mcp = new("twig-mcp");
    internal static readonly CompanionTool Tui = new("twig-tui");
    internal static IReadOnlyList<CompanionTool> All { get; } = [Mcp, Tui];
}
```

This centralizes companion definitions so `SelfUpdater`, `SelfUpdateCommand`, and the
first-run check all reference the same source of truth.

#### 2. IFileSystem Extension

Add `ReadAllText` and `WriteAllText` to `IFileSystem` to maintain the testability pattern
established by `SelfUpdater`. The current interface only has stream-based I/O
(`FileOpenRead`/`FileCreate`), and mixing `IFileSystem` with raw `File.ReadAllText`/
`File.WriteAllText` would break testability.

```csharp
// Added to IFileSystem
string ReadAllText(string path);
void WriteAllText(string path, string contents);

// Added to DefaultFileSystem
public string ReadAllText(string path) => File.ReadAllText(path);
public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
```

#### 3. Repo Slug Resolution Helper

The `repoSlug` is currently resolved from `AssemblyMetadataAttribute("GitHubRepo")` in
`CommandRegistrationModule.AddSelfUpdateCommands()`. The first-run check needs the same
value but runs outside the DI lifecycle. Extract to a shared static helper:

```csharp
// In Twig.Infrastructure/GitHub/RepoSlugResolver.cs (or inline in CompanionTool.cs)
internal static class RepoSlugResolver
{
    private const string DefaultSlug = "PolyphonyRequiem/twig";

    internal static string Resolve(System.Reflection.Assembly? assembly = null)
    {
        assembly ??= typeof(RepoSlugResolver).Assembly;
        var attrs = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
        foreach (var attr in attrs)
        {
            if (attr is System.Reflection.AssemblyMetadataAttribute meta
                && meta.Key == "GitHubRepo" && meta.Value is not null)
                return meta.Value;
        }
        return DefaultSlug;
    }
}
```

`CommandRegistrationModule.AddSelfUpdateCommands()` is refactored to use this helper,
eliminating the duplicated logic.

#### 4. IGitHubReleaseService — Tag-Matched Lookup

Add `GetReleaseByTagAsync` to `IGitHubReleaseService` to prevent version skew during
first-run companion install:

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

#### 5. SelfUpdater Enhancement

The `UpdateBinaryAsync` method is modified to accept additional binary names and extract
them alongside the main binary:

**Signature change:**
```csharp
// Before
public async Task<string> UpdateBinaryAsync(string downloadUrl, string archiveName, CancellationToken ct)

// After
public async Task<UpdateResult> UpdateBinaryAsync(
    string downloadUrl, string archiveName,
    IReadOnlyList<string>? companionExeNames = null,
    CancellationToken ct = default)
```

**New return type:**
```csharp
public sealed record UpdateResult(
    string MainBinaryPath,
    IReadOnlyList<CompanionUpdateResult> Companions);

public sealed record CompanionUpdateResult(string Name, bool Found, string? InstalledPath);
```

**Companion extraction logic** (added after main binary replacement):
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
                var oldPath = Path.Combine(dir, companion.GetExeName() + ".old");
                try { if (fs.FileExists(oldPath)) fs.FileDelete(oldPath); } catch { }
            }
        }
    }
}
```

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

#### 7. First-Run Companion Check

A new static class `CompanionFirstRunCheck` in `Twig.Infrastructure/GitHub/`:

```csharp
internal static class CompanionFirstRunCheck
{
    internal static void EnsureCompanions(
        IFileSystem fileSystem, string? processPath, string currentVersion)
    {
        if (processPath is null) return;
        var dir = Path.GetDirectoryName(processPath);
        if (dir is null) return;

        var versionFile = Path.Combine(dir, ".twig-version");

        // Check version marker (using IFileSystem for testability)
        if (fileSystem.FileExists(versionFile))
        {
            var storedVersion = fileSystem.ReadAllText(versionFile).Trim();
            if (storedVersion == currentVersion) return;
        }

        // Check companions
        var allPresent = true;
        foreach (var companion in CompanionTools.All)
        {
            if (!fileSystem.FileExists(Path.Combine(dir, companion.GetExeName())))
            {
                allPresent = false;
                break;
            }
        }

        // Write version marker (even if companions are missing)
        fileSystem.WriteAllText(versionFile, currentVersion);

        if (!allPresent)
        {
            // Schedule background companion download
            _ = Task.Run(() => DownloadMissingCompanionsAsync(dir, currentVersion));
        }
    }
}
```

**Repo slug resolution**: `DownloadMissingCompanionsAsync` uses `RepoSlugResolver.Resolve()`
to get the repo slug, then constructs a `GitHubReleaseClient` with a new `HttpClient`.
This avoids coupling with the DI container.

**Tag-matched download**: `DownloadMissingCompanionsAsync` calls
`GetReleaseByTagAsync($"v{currentVersion}")` (not `GetLatestReleaseAsync`) to ensure
companions match the installed twig version.

**Atomic writes**: Background download extracts to a temp directory, then moves each
companion binary to the target (temp → rename → move) per NF5.

Failures are silently caught and do not affect command execution.

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
  │   │       └─ Missing → download archive, extract companions only
  │   └─ Print upgrade summary (main + companions)
  │
  └─ Exit

User runs any "twig" command (startup)
  │
  ├─ SelfUpdater.CleanupOldBinary() — clean .old files (main + companions)
  ├─ CompanionFirstRunCheck.EnsureCompanions() — [NEW]
  │   ├─ Read .twig-version marker (via IFileSystem)
  │   ├─ Version matches? → skip
  │   ├─ Check companion binaries exist
  │   ├─ All present? → write marker, skip
  │   └─ Missing? → write marker, fire background download
  │       └─ Uses GetReleaseByTagAsync(v{currentVersion}) for version-matched companions
  └─ Continue to command execution (unblocked)
```

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Bundle vs. separate archives | **Bundle** all binaries in one archive | Version sync, single download, matches `publish-local.ps1` pattern |
| Companion list location | `CompanionTools` static class in `Infrastructure.GitHub` | Shared by `SelfUpdater`, `SelfUpdateCommand`, and startup check |
| Return type change | `UpdateResult` record | Backward-incompatible but only caller is `SelfUpdateCommand` (internal) |
| First-run network call | Fire-and-forget background task with atomic writes | Must not block startup or affect exit codes (NF1, NF2); temp+move prevents corruption (NF5) |
| Windows rename for companions | Apply rename trick to companions too | `twig-mcp` may be running as MCP server during upgrade |
| twig-tui publishing | `PublishSingleFile=true, SelfContained=true, PublishTrimmed=false` | Terminal.Gui v2 uses reflection extensively — no AOT, no trimming. Expect ~60–80 MB binary. |
| Version-matched downloads | `GetReleaseByTagAsync()` for first-run check | Prevents version skew: user on v1.2 won't accidentally get v1.3 companions |
| File I/O testability | Extend `IFileSystem` with `ReadAllText`/`WriteAllText` | Maintains testability pattern; avoids mixing `IFileSystem` with raw `File.*` calls |
| Repo slug sharing | `RepoSlugResolver` static helper | Eliminates duplication between `CommandRegistrationModule` and first-run check |

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

### No first-run check (upgrade-only)

Only install/upgrade companions via explicit `twig upgrade` or installer scripts. No startup
check for missing companions.

**Pros:**
- Simpler implementation, no startup overhead
- No background network calls

**Cons:**
- Doesn't satisfy acceptance criterion 2 of parent Issue #1643: "First-run after upgrade
  installs any missing companions automatically"
- Users who manually install `twig` would never get companions

**Decision:** Rejected. The first-run check is a safety net that ensures companions are
available regardless of installation method.

---

## Dependencies

### External
- **GitHub Releases API**: Used for both upgrade and first-run companion download.
  First-run check uses the `/releases/tags/{tag}` endpoint for version-matched lookup.
- **MinVer**: All three projects derive version from git tags (existing dependency)
- **Terminal.Gui v2**: Must support `PublishSingleFile` for `twig-tui` bundling (see Open
  Question #1; CI validation task T-1645-2 explicitly tests this)

### Internal
- **`SelfUpdater`**: Must be extended before `SelfUpdateCommand` can use it
- **`IFileSystem`**: Must be extended with `ReadAllText`/`WriteAllText` before first-run check
- **`IGitHubReleaseService`**: Must add `GetReleaseByTagAsync` before first-run check
- **`RepoSlugResolver`**: Must exist before first-run check (and refactored into
  `CommandRegistrationModule`)
- **`release.yml`**: Must bundle companions before upgrade can install them (deploy dependency)
- **`CompanionTools` registry**: Must exist before any consumer can reference it

### Sequencing
1. `CompanionTools` registry + `IFileSystem` extension → `SelfUpdater` changes → `SelfUpdateCommand` changes → tests
2. `release.yml` changes can proceed in parallel with code changes
3. Installer script changes depend on `release.yml` (scripts extract what the pipeline bundles)
4. First-run check depends on `CompanionTools` registry, `IFileSystem` extension, `RepoSlugResolver`, and `GetReleaseByTagAsync`

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `IFileSystem` / `DefaultFileSystem` | Add `ReadAllText` / `WriteAllText` methods |
| `IGitHubReleaseService` / `GitHubReleaseClient` | Add `GetReleaseByTagAsync` method |
| `SelfUpdater` | Return type change, companion extraction with atomic writes |
| `SelfUpdateCommand` | Companion-aware upgrade flow, "install missing" when current |
| `CommandRegistrationModule` | Refactor repo slug resolution to use `RepoSlugResolver` |
| `Program.cs` | First-run companion check added after existing cleanup |
| `install.ps1` | Verification of companion binaries added |
| `install.sh` | Verification and `chmod +x` for companions added |
| `release.yml` | Build + bundle steps for twig-mcp and twig-tui added |
| `publish-local.ps1` | twig-tui publish step added; `Invoke-Publish` label updated |
| `Twig.Tui.csproj` | `PublishSingleFile=true`, `SelfContained=true`, `PublishTrimmed=false` |

### Backward Compatibility

- **`SelfUpdater.UpdateBinaryAsync`** return type changes from `string` to `UpdateResult`.
  Only caller is `SelfUpdateCommand` (internal). No external consumers.
- **Release archives** will now contain additional files. Old versions of `twig upgrade` will
  extract the archive but only look for `twig.exe` — companions are ignored. No breaking change.
- **Installer scripts** will now verify more binaries. Old archives without companions will
  cause verification warnings (not errors).

### Performance

- **Upgrade**: Archive download is larger. twig-mcp adds ~15 MB (AOT binary); twig-tui adds
  ~60–80 MB (`PublishSingleFile`, self-contained, no trimming). Total archive size per
  platform could reach 80–100 MB compressed. Single download vs. current single download.
- **Startup**: First-run check adds one `File.Exists()` call per companion + one file read
  for version marker. Negligible overhead (~1ms).
- **Background download**: Only runs once per version, only when companions are missing.
  Does not block command execution.

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
| **Future enhancement** | If GitHub releases ever include checksum files (e.g., `SHA256SUMS`), validation can be added to `SelfUpdater` without changing the public API. Tracked as a potential follow-up. |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| twig-tui `PublishSingleFile` incompatibility with Terminal.Gui | Medium | High | CI validation task T-1645-2 publishes twig-tui as SingleFile and runs smoke test. **User decision**: attempt inclusion; if SingleFile publish fails or produces runtime errors, exclude twig-tui from the bundle and file a follow-up issue (see Resolved Questions). twig-mcp is unaffected. |
| twig-tui trim warnings at publish time | Medium | **High** | `PublishTrimmed=false` set explicitly in `Twig.Tui.csproj`. Terminal.Gui v2 relies on reflection for property binding, view construction, and event handling — trimming causes silent runtime failures. If any transitive dependency enables trimming, add `<TrimMode>partial</TrimMode>` override in csproj. CI must verify a clean publish with zero trim warnings. |
| Archive size increase (80–100 MB compressed) slows download | Medium | Low | twig-tui at ~60–80 MB dominates. Monitor download times. If unacceptable, consider splitting twig-tui to a separate optional archive in a follow-up. |
| `twig-mcp` locked by running MCP server during upgrade | Medium | Low | Windows rename trick handles this (same as main binary). User must restart MCP server. |
| Background first-run download fails silently | Low | Low | By design (NF2). User can always run `twig upgrade` explicitly. |
| Version marker file corruption | Low | Low | If unreadable, treat as version change and re-check companions. |
| Interrupted download leaves corrupted companion | Low | Medium | Atomic write pattern (NF5): download to temp file, move to target. Partial downloads never appear as the final binary. |

---

## Resolved Questions

| # | Question | Resolution |
|---|----------|------------|
| 1 | Does Terminal.Gui v2 support `PublishSingleFile=true` without trimming? | **Resolved (user decision)**: Attempt inclusion with `PublishSingleFile=true, PublishTrimmed=false`. Task T-1645-2 validates via CI smoke test. If SingleFile publish fails or produces runtime errors, twig-tui is excluded from the bundle and a follow-up issue is filed. This is acceptable because twig-tui is the newest companion and its absence does not block core twig or twig-mcp functionality. |

## Open Questions

| # | Question | Severity | Notes |
|---|----------|----------|-------|
| 2 | Should the first-run companion download show a progress indicator? | Low | Current design is fire-and-forget. Could add a one-line message: "Installing companion tools..." — but this may confuse users if it appears during unrelated commands. |

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `src/Twig.Infrastructure/GitHub/CompanionTool.cs` | `CompanionTool` record, `CompanionTools` static registry, `UpdateResult`/`CompanionUpdateResult` records, `RepoSlugResolver` static helper |
| `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | Static class implementing first-run companion detection and background download |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | Unit tests for `CompanionTool` registry, `RepoSlugResolver`, and `CompanionFirstRunCheck` |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig.Infrastructure/GitHub/IFileSystem.cs` | Add `ReadAllText(string)` and `WriteAllText(string, string)` to `IFileSystem`; implement in `DefaultFileSystem` |
| `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | Return type `string` → `UpdateResult`; companion extraction with atomic writes; `CleanupOldBinary` cleans companion `.old` files |
| `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | Add `GetReleaseByTagAsync(string tag, CancellationToken)` method |
| `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | Implement `GetReleaseByTagAsync` using `/releases/tags/{tag}` endpoint |
| `src/Twig/Commands/SelfUpdateCommand.cs` | Build companion name list; handle `UpdateResult`; "install missing companions" path when already current |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Refactor `AddSelfUpdateCommands` to use `RepoSlugResolver` |
| `src/Twig/Program.cs` | Add `CompanionFirstRunCheck.EnsureCompanions()` call after `SelfUpdater.CleanupOldBinary()` |
| `src/Twig.Tui/Twig.Tui.csproj` | Add `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`, `<PublishTrimmed>false</PublishTrimmed>` |
| `.github/workflows/release.yml` | Add `dotnet publish` steps for twig-mcp (AOT) and twig-tui (SingleFile, no trim); bundle into existing archive |
| `install.ps1` | Verify `twig-mcp.exe` and `twig-tui.exe` after extraction; print companion versions |
| `install.sh` | Verify `twig-mcp` and `twig-tui` after extraction; `chmod +x`; print companion versions |
| `publish-local.ps1` | Add `Invoke-Publish "twig-tui"` step; update function label for mixed publish strategies; print twig-tui version |
| `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs` | Add tests for companion extraction from zip/tar.gz archives; update existing tests for `UpdateResult` return type |
| `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | Add tests for companion upgrade/install flow |

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
| T-1644-1 | **Create CompanionTool registry and shared helpers** — Define `CompanionTool` record, `CompanionTools` static class listing known companions (`twig-mcp`, `twig-tui`), `UpdateResult` and `CompanionUpdateResult` record types, `RepoSlugResolver` static helper. Extend `IFileSystem` with `ReadAllText`/`WriteAllText` and implement in `DefaultFileSystem`. | `src/Twig.Infrastructure/GitHub/CompanionTool.cs`, `src/Twig.Infrastructure/GitHub/IFileSystem.cs` | S | — |
| T-1644-2 | **Add `GetReleaseByTagAsync` to release service** — Add method to `IGitHubReleaseService` interface and implement in `GitHubReleaseClient` using the `/releases/tags/{tag}` GitHub API endpoint. Add unit test. | `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs`, `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs`, `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs` | S | F5 |
| T-1644-3 | **Extend SelfUpdater for companions** — Modify `UpdateBinaryAsync` to accept `IReadOnlyList<string>? companionExeNames`, find and copy each companion from the extracted archive using atomic writes (temp → move). Apply Windows rename trick for locked companions. Change return type to `UpdateResult`. Extend `CleanupOldBinary` to also clean companion `.old` files. | `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | M | F1, NF5 |
| T-1644-4 | **Update SelfUpdateCommand for companion-aware upgrades** — Build companion exe name list from `CompanionTools.All`. Pass to `UpdateBinaryAsync`. Handle `UpdateResult` to report companion upgrades. Add "install missing companions" path when main binary is already current. Refactor `CommandRegistrationModule.AddSelfUpdateCommands` to use `RepoSlugResolver`. | `src/Twig/Commands/SelfUpdateCommand.cs`, `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | M | F1, F2, F3 |
| T-1644-5 | **Add unit tests for companion upgrade flow** — Tests for: (a) SelfUpdater extracts companions from zip, (b) SelfUpdater extracts companions from tar.gz, (c) SelfUpdater handles missing companions in archive, (d) SelfUpdater uses atomic writes, (e) CleanupOldBinary cleans companion `.old` files, (f) SelfUpdateCommand reports companion results, (g) SelfUpdateCommand installs missing companions when current, (h) GetReleaseByTagAsync returns correct release. | `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs`, `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | L | — |

**Acceptance Criteria:**
- [ ] `SelfUpdater.UpdateBinaryAsync` extracts all companion binaries found in the archive using atomic writes
- [ ] `SelfUpdater.CleanupOldBinary` removes companion `.old` files
- [ ] `SelfUpdateCommand` reports which companions were upgraded or not found
- [ ] `twig upgrade` installs missing companions even when main binary is up to date
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
| T-1645-1 | **Add companion publish steps to release.yml** — After publishing `src/Twig/Twig.csproj`, add `dotnet publish` for `src/Twig.Mcp/Twig.Mcp.csproj` (same AOT flags: `-c Release -r ${{ matrix.rid }} --self-contained true`) and `src/Twig.Tui/Twig.Tui.csproj` (non-AOT: `-c Release -r ${{ matrix.rid }} --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false /p:PublishAot=false`). Flags apply identically across all four platform RIDs (win-x64, linux-x64, osx-x64, osx-arm64) — no platform-specific flag variations needed. All three publish to the same `./publish/${{ matrix.rid }}/` directory so the existing archive step bundles them together. | `.github/workflows/release.yml` | M | F8 |
| T-1645-2 | **Add PublishSingleFile to Twig.Tui.csproj** — Add `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`, and `<PublishTrimmed>false</PublishTrimmed>` (Terminal.Gui v2 uses reflection extensively — trimming causes runtime failures). Verify clean publish locally. This task serves as CI validation for Open Question #1. | `src/Twig.Tui/Twig.Tui.csproj` | S | F8, NF3 |
| T-1645-3 | **Update install.ps1 to verify companions** — After extracting the archive, verify that `twig-mcp.exe` and `twig-tui.exe` exist in the install directory. Print their versions alongside the main twig version. Use warnings (not errors) if companions are missing (older archives). | `install.ps1` | S | F6 |
| T-1645-4 | **Update install.sh to verify companions** — After extracting the archive, verify that `twig-mcp` and `twig-tui` exist. Run `chmod +x` on each. Print their versions alongside the main twig version. Use warnings for missing companions. | `install.sh` | S | F7 |
| T-1645-5 | **Update publish-local.ps1 to include twig-tui** — Add `Invoke-Publish "twig-tui" "src\Twig.Tui\Twig.Tui.csproj"` call. Update `Invoke-Publish` function label from "AOT, win-x64" to reflect that publish strategy varies by project (twig-tui is non-AOT SingleFile). Print twig-tui version at end. | `publish-local.ps1` | S | — |

**Acceptance Criteria:**
- [ ] `release.yml` builds and bundles twig, twig-mcp, and twig-tui for all four platform RIDs
- [ ] twig-tui publishes as a single-file self-contained binary with trimming disabled
- [ ] twig-tui publish step specifies correct non-AOT flags on all platforms including Linux/macOS
- [ ] `install.ps1` verifies all three binaries and prints their versions
- [ ] `install.sh` verifies, `chmod +x`, and prints versions for all three binaries
- [ ] `publish-local.ps1` publishes all three binaries to `~/.twig/bin/` with correct labels
- [ ] Missing companions in older archives produce warnings, not errors

---

### Issue #1646: Add first-run install of missing companions on next twig upgrade

**Goal:** Implement a lightweight first-run check that detects missing companion binaries
after a version change and installs them automatically in the background.

**Prerequisites:** #1644 (CompanionTool registry, `IFileSystem` extension, `RepoSlugResolver`,
and `GetReleaseByTagAsync` must exist).

**Tasks:**

| Task ID | Description | Files | Effort | Satisfies |
|---------|-------------|-------|--------|-----------|
| T-1646-1 | **Implement CompanionFirstRunCheck** — Static class with `EnsureCompanions(IFileSystem, string? processPath, string currentVersion)` method. Reads `.twig-version` marker using `IFileSystem.ReadAllText` (not raw `File.ReadAllText`). If version mismatch, checks for missing companions. If missing, fires background download task. Version marker always written via `IFileSystem.WriteAllText`. | `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | M | F4, NF1 |
| T-1646-2 | **Implement background companion download** — Private async method in `CompanionFirstRunCheck` that uses `RepoSlugResolver.Resolve()` to get repo slug, creates `GitHubReleaseClient` with a new `HttpClient`, calls `GetReleaseByTagAsync($"v{currentVersion}")` for version-matched lookup, downloads the archive, and extracts only missing companion binaries using atomic writes (temp → move). All failures are caught and swallowed (best-effort, NF2). | `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | M | F5, NF2, NF5 |
| T-1646-3 | **Integrate first-run check into Program.cs** — Call `CompanionFirstRunCheck.EnsureCompanions()` after `SelfUpdater.CleanupOldBinary()` in `Program.cs`. Pass `VersionHelper.GetVersion()` as the current version and `new DefaultFileSystem()` as the file system. | `src/Twig/Program.cs` | S | F4 |
| T-1646-4 | **Add unit tests for first-run check** — Tests for: (a) version marker matches → no action, (b) version marker missing → checks companions, (c) version marker mismatch → checks companions, (d) all companions present → writes marker only, (e) companion missing → triggers download, (f) download failure → swallowed, marker still written, (g) all I/O goes through `IFileSystem` (no raw `File.*` calls). | `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | M | NF1, NF2, NF3 |

**Acceptance Criteria:**
- [ ] First-run check does not execute when version marker matches current version
- [ ] First-run check uses `IFileSystem` for all file I/O (fully testable, no raw `File.*`)
- [ ] First-run companion download uses `GetReleaseByTagAsync` for version-matched lookup
- [ ] First-run check writes version marker even when companions are missing
- [ ] Missing companions trigger a background download that does not block command execution
- [ ] Background download uses atomic writes (temp → move) to prevent corrupted binaries
- [ ] Download failures do not affect command exit codes or output
- [ ] All new code is AOT-compatible and passes `TreatWarningsAsErrors`

---

## PR Groups

### PG-1: Companion upgrade infrastructure (deep)

**Tasks:** T-1644-1, T-1644-2, T-1644-3, T-1644-5 (SelfUpdater portion)
**Issues:** #1644
**Classification:** Deep — few files, complex extraction and platform-specific logic
**Estimated LoC:** ~450 (implementation + tests)
**Files:** ~8

| File | Type |
|------|------|
| `src/Twig.Infrastructure/GitHub/CompanionTool.cs` | New |
| `src/Twig.Infrastructure/GitHub/IFileSystem.cs` | Modified |
| `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | Modified |
| `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | Modified |
| `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/SelfUpdaterTests.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | New |

**Successors:** PG-2, PG-4

---

### PG-2: Companion-aware upgrade command (deep)

**Tasks:** T-1644-4, T-1644-5 (SelfUpdateCommand portion)
**Issues:** #1644
**Classification:** Deep — complex command flow with "install missing" path
**Estimated LoC:** ~250 (implementation + tests)
**Files:** ~3

| File | Type |
|------|------|
| `src/Twig/Commands/SelfUpdateCommand.cs` | Modified |
| `src/Twig/DependencyInjection/CommandRegistrationModule.cs` | Modified |
| `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | Modified |

**Predecessors:** PG-1
**Successors:** None (PG-4 depends only on PG-1)

---

### PG-3: Release pipeline and installer scripts (wide)

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

**Predecessors:** None (parallel with PG-1 and PG-2)
**Successors:** None

---

### PG-4: First-run companion check (deep)

**Tasks:** T-1646-1, T-1646-2, T-1646-3, T-1646-4
**Issues:** #1646
**Classification:** Deep — few files, async networking + platform concerns
**Estimated LoC:** ~350 (implementation + tests)
**Files:** ~3

| File | Type |
|------|------|
| `src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs` | New |
| `src/Twig/Program.cs` | Modified |
| `tests/Twig.Infrastructure.Tests/GitHub/CompanionToolTests.cs` | Modified |

**Predecessors:** PG-1 (requires CompanionTools registry, IFileSystem extension, RepoSlugResolver, GetReleaseByTagAsync)

---

## Execution Order

```
PG-1 (Companion infrastructure)
  ├──→ PG-2 (Upgrade command)
  └──→ PG-4 (First-run check)
PG-3 (Pipeline + scripts) [parallel with PG-1, PG-2, and PG-4]
```

PG-4 depends only on PG-1 (not PG-2 or PG-3). PG-4 unit tests are independently
testable against the infrastructure layer. Integration testing with the full pipeline
(PG-3) can be done after all PRs merge.

Total estimated LoC across all PR groups: **~1,250**
