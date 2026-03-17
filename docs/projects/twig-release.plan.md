# Twig Release & Distribution System — Solution Design

> **Revision**: 4 — addresses technical review feedback (score: 87/100)  
> **Date**: 2026-03-15  
> **Author**: Architecture — generated from codebase analysis  
> **Status**: DRAFT

---

## Executive Summary

This document defines the architecture and implementation plan for a cross-platform release and distribution system for the Twig CLI tool. Twig is a .NET 9 Native AOT CLI (single-file, no runtime dependency) that manages Azure DevOps work items. Today at version 0.1.0, the project has zero release infrastructure — no git remote, no CI/CD, no install scripts, no self-update mechanism. This design introduces: (1) automatic semantic versioning via MinVer driven by git tags, (2) GitHub Actions CI/CD pipelines for build-on-PR and tag-triggered multi-platform releases, (3) platform-specific install scripts for one-liner installation, (4) a `twig upgrade` self-update command that downloads and replaces the running binary, (5) auto-generated changelogs from conventional commits, and (6) a `twig changelog` command. The system targets four RIDs: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64` and is designed for eventual public GitHub distribution.

---

## Background

### Current State

The Twig codebase is a three-project .NET solution:

| Project | Purpose | Key Dependencies |
|---------|---------|-----------------|
| `src/Twig` | CLI host — ConsoleAppFramework v5.7.13 commands, Program.cs entry point | ConsoleAppFramework, contoso.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3 |
| `src/Twig.Domain` | Domain model — aggregates, value objects, services, interfaces | None (pure .NET) |
| `src/Twig.Infrastructure` | Infrastructure — ADO REST client, SQLite persistence, auth providers, config | contoso.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3 |

Tests: `Twig.Cli.Tests`, `Twig.Domain.Tests`, `Twig.Infrastructure.Tests` (xUnit + Shouldly + NSubstitute).

**Build profile** (`src/Twig/Twig.csproj`):
- `PublishAot=true`, `PublishTrimmed=true`, `TrimMode=full`, `StripSymbols=true`
- `InvariantGlobalization=true`, `JsonSerializerIsReflectionEnabledByDefault=false`
- AOT-compatible JSON via `TwigJsonContext` source generator

**Versioning**: Hardcoded `<Version>0.1.0</Version>` in `Directory.Build.props` (line 9). `VersionHelper` in `Program.cs` reads `AssemblyInformationalVersionAttribute` at runtime, strips build metadata after `+`, falls back to `"0.1.0"`. This fallback becomes dead code after MinVer integration (MinVer always sets the attribute) and should be updated to `"0.0.0"` to avoid confusion.

**SDK**: `global.json` pins .NET SDK `10.0.104` with `rollForward: latestMinor`.

**No release infrastructure exists**: No GitHub remote, no CI/CD workflows, no changelog, no install scripts.

### Context

The tool is currently used internally. To distribute it reliably across team members (and eventually publicly), it needs automated versioning, CI/CD, install scripts, and self-update capability. Native AOT adds complexity: cross-OS compilation is not supported (you cannot build a Linux Native AOT binary on Windows), so each platform must build on its native runner.

---

## Problem Statement

1. **No automated versioning**: Version is hardcoded at `0.1.0`. Every release requires manual edit to `Directory.Build.props` — error-prone and out of sync with release tags.
2. **No build/release pipeline**: No CI to validate PRs, no automated release to produce platform-specific binaries. Distribution is ad-hoc.
3. **No installation path**: Users must manually download, extract, and configure PATH. This is fragile and discourages adoption.
4. **No update mechanism**: Users must manually check for updates, download, and replace binaries. On Windows, the running executable is file-locked.
5. **No changelog**: No visibility into what changed between versions. Users upgrading have no context.

---

## Goals and Non-Goals

### Goals

| ID | Goal | Measure |
|----|------|---------|
| G-1 | Zero-touch semantic versioning from git tags | `git tag 1.0.0 && git push --tags` produces version 1.0.0 in binaries with no file edits |
| G-2 | CI validates every PR (build + test) | GitHub Actions `ci.yml` runs on `pull_request` to `main` |
| G-3 | Tag push triggers multi-platform release | `release.yml` produces 4 archives + GitHub Release with changelog |
| G-4 | One-liner install on all platforms | `irm .../install.ps1 \| iex` (Windows), `curl ... \| bash` (Linux/macOS) |
| G-5 | In-place self-update via `twig upgrade` | CLI checks GitHub Releases API, downloads correct platform binary, replaces itself |
| G-6 | Auto-generated changelog from conventional commits | CHANGELOG.md generated, included in GitHub Release notes, viewable via `twig changelog` |
| G-7 | Design for public GitHub from the start | No internal-only assumptions; install URLs use GitHub Releases API |

### Non-Goals

| ID | Non-Goal | Rationale |
|----|----------|-----------|
| NG-1 | WinGet / Homebrew / APT package publishing | Future option; stub only in this design |
| NG-2 | Nightly / rolling release builds | Only tag-triggered releases |
| NG-3 | Code signing | Can be layered on later; not blocking distribution |
| NG-4 | Docker image distribution | CLI tool, not a service |
| NG-5 | Automatic version bump from commit messages | MinVer derives version from tags; conventional commits drive *human* tagging decisions and changelog, not automatic version calculation |

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-001 | Remove hardcoded `Version=0.1.0` from `Directory.Build.props`; version derived from git tags via MinVer | High |
| FR-002 | CI workflow: build all projects + run all tests on `pull_request` to `main` | High |
| FR-003 | Release workflow: trigger on `v*` tag push; build Native AOT for 4 RIDs on native runners | High |
| FR-004 | Release workflow: create GitHub Release with auto-generated release notes from conventional commits | High |
| FR-005 | Release workflow: attach `twig-{rid}.zip` (Windows) and `twig-{rid}.tar.gz` (Linux/macOS) archives | High |
| FR-006 | `install.ps1`: download latest release, extract to `~/.twig/bin/`, add to user PATH if absent | High |
| FR-007 | `install.sh`: download latest release, detect platform (x64/arm64, Linux/macOS), extract to `~/.twig/bin/`, add to PATH via shell profile | High |
| FR-008 | `twig upgrade`: check GitHub Releases API for newer version, download correct platform archive, replace binary in-place. Named `upgrade` (not `update`) because `twig update <field> <value>` is already used for work item field updates (see DD-9). | High |
| FR-009 | `twig upgrade` on Windows: handle file-lock by renaming running exe before replacing | High |
| FR-010 | `twig upgrade`: display changelog delta (what changed between current and new version) | Medium |
| FR-011 | `twig changelog`: display recent changelog entries from embedded or fetched changelog data | Medium |
| FR-012 | Auto-generate `CHANGELOG.md` from conventional commits during release workflow | Medium |
| FR-013 | `twig version` and `--version` continue to work, now showing MinVer-derived version | High |

### Non-Functional Requirements

| ID | Requirement | Metric |
|----|-------------|--------|
| NFR-001 | Self-update must work with Native AOT binaries (no .NET runtime dependency) | Binary is single-file, no runtime needed |
| NFR-002 | Install scripts must be idempotent | Running install twice has same result |
| NFR-003 | Release build time < 15 minutes per platform | GitHub Actions runner performance |
| NFR-004 | All new code must be AOT-compatible | No reflection, source-generated JSON only |
| NFR-005 | GitHub API calls use unauthenticated public endpoints | No tokens required for public release queries |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Developer Workflow                           │
│                                                                     │
│  feat: add widget ──► git commit ──► git push ──► PR ──► CI        │
│                                                                     │
│  git tag v1.2.0 ──► git push --tags ──► Release Workflow           │
└─────────────┬───────────────────────────────────┬───────────────────┘
              │                                   │
              ▼                                   ▼
┌──────────────────────┐          ┌───────────────────────────────────┐
│   CI Workflow        │          │   Release Workflow                │
│   (.github/workflows │          │   (.github/workflows             │
│    /ci.yml)          │          │    /release.yml)                  │
│                      │          │                                   │
│  • checkout          │          │  • checkout (fetch-depth: 0)      │
│  • dotnet restore    │          │  • matrix: 4 RIDs × native OS    │
│  • dotnet build      │          │  • dotnet publish -r {rid}       │
│  • dotnet test       │          │  • archive (zip/tar.gz)          │
│                      │          │  • generate changelog            │
│  Triggers:           │          │  • create GitHub Release         │
│    pull_request →    │          │  • attach archives               │
│    main              │          │                                   │
└──────────────────────┘          │  Triggers: push tags v*          │
                                  └──────────────────────────────────┘
                                              │
                                              ▼
                                  ┌───────────────────────┐
                                  │   GitHub Release       │
                                  │                        │
                                  │  • twig-win-x64.zip   │
                                  │  • twig-linux-x64     │
                                  │    .tar.gz            │
                                  │  • twig-osx-x64       │
                                  │    .tar.gz            │
                                  │  • twig-osx-arm64     │
                                  │    .tar.gz            │
                                  │  • Release notes      │
                                  │    (changelog)        │
                                  └─────────┬─────────────┘
                                            │
                          ┌─────────────────┼─────────────────┐
                          ▼                 ▼                 ▼
                   ┌────────────┐   ┌────────────┐   ┌──────────────┐
                   │ install.ps1│   │ install.sh │   │ twig upgrade  │
                   │ (Windows)  │   │ (Linux/Mac)│   │ (self-update)│
                   │            │   │            │   │              │
                   │ → ~/.twig/ │   │ → ~/.twig/ │   │ → in-place   │
                   │   bin/twig │   │   bin/twig │   │   binary     │
                   │   .exe     │   │            │   │   replace    │
                   └────────────┘   └────────────┘   └──────────────┘
```

### Key Components

#### 1. MinVer Integration (Versioning)

**Responsibility**: Derive semantic version from git tags at build time, inject into assembly metadata.

**Design**:
- Add `MinVer` (v7.0.0) as a `PackageVersion` in `Directory.Packages.props` and reference in `Directory.Build.props` with `PrivateAssets="All"`.
- Remove `<Version>0.1.0</Version>` from `Directory.Build.props`.
- `VersionHelper.GetVersion()` in `Program.cs` already reads `AssemblyInformationalVersionAttribute` and strips `+` metadata — this continues to work unchanged with MinVer.
- Tag convention: `v1.0.0` (using `MinVerTagPrefix=v`).
- MinVer will auto-calculate pre-release versions between tags (e.g., `1.0.1-alpha.0.3` = 3 commits after `v1.0.0`).

**Why MinVer over alternatives**: MinVer is the simplest option — zero configuration, one NuGet package, reads git tags at build time. Nerdbank.GitVersioning requires a `version.json` file and more complex setup. GitVersion requires a YAML config. MinVer fits the "zero manual version management" requirement perfectly.

#### 2. CI Workflow (`.github/workflows/ci.yml`)

**Responsibility**: Validate every PR — restore, build, test.

**Design**:
```yaml
name: CI
on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
          filter: tree:0
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet test --no-build --settings test.runsettings
```

**Notes**:
- `fetch-depth: 0` is required for MinVer to find version tags.
- `filter: tree:0` is a **treeless partial clone** optimization: it downloads all commit objects (needed by MinVer for tag walking and height calculation) but defers tree and blob objects until Git needs them (e.g., during checkout or build). This significantly reduces clone size for large repos while still providing full commit history. File content is lazily fetched via additional round-trips when the build step accesses source files — this is handled transparently by Git and has negligible impact on build time.
- Runs on `ubuntu-latest` — sufficient for build+test (non-AOT build).
- Uses `test.runsettings` to exclude `Category=Interactive` tests.

#### 3. Release Workflow (`.github/workflows/release.yml`)

**Responsibility**: Build Native AOT binaries for 4 platforms, create GitHub Release with changelog.

**Design**: Matrix strategy with native runners per OS (required because Native AOT does not support cross-OS compilation).

| RID | Runner | Archive Format |
|-----|--------|----------------|
| `win-x64` | `windows-latest` | `.zip` |
| `linux-x64` | `ubuntu-latest` | `.tar.gz` |
| `osx-x64` | `macos-13` | `.tar.gz` |
| `osx-arm64` | `macos-latest` (ARM64) | `.tar.gz` |

**Workflow structure**:
1. **`build` job** (matrix): Checkout → setup .NET → `dotnet publish` with Native AOT → archive → upload artifact.
2. **`release` job** (needs: `build`): Download all artifacts → generate changelog → create GitHub Release → attach archives.

**Important**: The workflow must declare `permissions: contents: write` at the workflow or job level. Since GitHub tightened default `GITHUB_TOKEN` permissions in 2022, the token defaults to read-only on many repository configurations. Without explicit `contents: write`, the `softprops/action-gh-release@v2` step will fail with HTTP 403 when creating the GitHub Release.

**Publish command per matrix entry**:
```bash
dotnet publish src/Twig/Twig.csproj -c Release -r ${{ matrix.rid }} \
  --self-contained true -o ./publish/${{ matrix.rid }}
```

**Archive naming**: `twig-{rid}.zip` (Windows), `twig-{rid}.tar.gz` (Linux/macOS).

**Changelog generation**: Use `git log --pretty=format:...` between previous tag and current tag to generate conventional-commit-based release notes. Additionally, enable GitHub's auto-generated release notes feature for the release.

#### 4. Install Scripts

##### `install.ps1` (Windows)

**Responsibility**: Download latest Twig release from GitHub, extract to `~/.twig/bin/`, add to user PATH.

**Design**:
```
1. Query GitHub Releases API: GET /repos/PolyphonyRequiem/twig/releases/latest
2. Find asset named 'twig-win-x64.zip'
3. Download to temp
4. Create ~/.twig/bin/ if not exists
5. Extract twig.exe to ~/.twig/bin/
6. Add ~/.twig/bin to user PATH (via [Environment]::SetEnvironmentVariable)
7. Verify: twig --version
```

**Idempotency**: Checks if `~/.twig/bin` is already in PATH before adding. Overwrites existing binary.

**Usage**: `irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex`

##### `install.sh` (Linux/macOS)

**Responsibility**: Same as `install.ps1` for Unix platforms.

**Design**:
```
1. Detect OS (uname -s) and architecture (uname -m)
2. Map to RID: linux-x64, osx-x64, osx-arm64
3. Query GitHub Releases API for latest
4. Download twig-{rid}.tar.gz
5. Extract to ~/.twig/bin/
6. chmod +x ~/.twig/bin/twig
7. Add ~/.twig/bin to PATH via shell profile (~/.bashrc, ~/.zshrc, etc.)
8. Verify
```

**Usage**: `curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash`

#### 5. Self-Update Command (`twig upgrade`)

**Responsibility**: Check for newer version, download, replace binary in-place.

**Architecture**: New `SelfUpdateCommand` in `src/Twig/Commands/`, new `IGitHubReleaseService` in `src/Twig.Domain/Interfaces/`, new `GitHubReleaseClient` in `src/Twig.Infrastructure/GitHub/`.

**Data flow**:
```
twig upgrade
    │
    ▼
SelfUpdateCommand
    │
    ├── Get current version (VersionHelper.GetVersion())
    │
    ├── IGitHubReleaseService.GetLatestReleaseAsync()
    │   └── GET https://api.github.com/repos/PolyphonyRequiem/twig/releases/latest
    │       → { tag_name: "v1.2.0", assets: [...], body: "changelog..." }
    │
    ├── Compare versions (SemVerComparer — see DD-12)
    │   └── If latest <= current: "Already up to date (v1.1.0)"
    │
    ├── Determine RID (RuntimeInformation.RuntimeIdentifier — returns compile-time
    │   target RID in AOT binaries; fallback to OS/Arch detection if empty)
    │
    ├── Download correct archive asset
    │   └── twig-{rid}.zip or twig-{rid}.tar.gz
    │
    ├── Extract to temp directory
    │
    ├── Replace binary:
    │   ├── Windows: Rename twig.exe → twig.old.exe, copy new → twig.exe
    │   └── Unix: Overwrite directly (Unix doesn't lock running executables)
    │
    ├── Clean up temp files and twig.old.exe (deferred on Windows)
    │
    └── Display changelog delta (release body from API)
```

**Windows file-lock strategy**: On Windows, a running `.exe` is locked for writes but *can be renamed*. The self-update:
1. Renames `twig.exe` → `twig.old.exe`
2. Copies new binary to `twig.exe`
3. Prints "Update complete. Restart to use v1.2.0."
4. On next launch, deletes `twig.old.exe` if it exists (cleanup)

**AOT considerations**:
- All GitHub API JSON deserialization must use source-generated `JsonSerializerContext` (add DTOs to `TwigJsonContext`).
- Every DTO property **MUST** have an explicit `[JsonPropertyName("snake_case_key")]` attribute because `TwigJsonContext` uses `CamelCase` naming policy, but the GitHub API returns snake_case keys. Without these attributes, deserialization silently produces empty/default values.
- DTO classes **MUST** be `internal sealed class` to match the established ADO DTO pattern and avoid unintended API surface exposure.
- HTTP calls use the existing `HttpClient` pattern.
- RID detection uses `RuntimeInformation.RuntimeIdentifier`, which in Native AOT returns the compile-time target RID (e.g., `linux-x64`, `win-x64`, `osx-arm64`) — simple, AOT-safe, and directly usable for asset name construction.

**New DTOs** (added to `TwigJsonContext`):

> **IMPORTANT**: The existing `TwigJsonContext` uses `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]`, which maps C# `PascalCase` properties to `camelCase` JSON keys (e.g., `TagName` → `tagName`). However, the GitHub Releases API returns **snake_case** keys (e.g., `tag_name`, `browser_download_url`). Without explicit `[JsonPropertyName]` attributes, these fields would always deserialize to their default values (empty string), silently breaking version comparison and download URL resolution. Every existing ADO DTO in the codebase (`AdoWorkItemResponse`, `AdoIterationResponse`, `AdoWorkItemTypeResponse`, etc.) uses explicit `[JsonPropertyName]` attributes on every property — this is an established codebase pattern and **MUST** be followed for the GitHub DTOs. Visibility is `internal sealed class` to match the established ADO DTO pattern.

```csharp
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
```

**`TwigJsonContext` registration**:
- Add `[JsonSerializable(typeof(GitHubRelease))]` — needed for deserializing the `/releases/latest` single-object response.
- Add `[JsonSerializable(typeof(List<GitHubRelease>))]` — needed for deserializing the `/releases?per_page={count}` endpoint, which returns a raw JSON array (not a wrapper object). Without this, the source-generated context has no metadata for this collection type, causing `NotSupportedException` at runtime in AOT mode.
- `GitHubAsset` and `List<GitHubAsset>` do **NOT** need explicit registration — source generation automatically handles property types of already-registered types. This is consistent with how `AdoBatchWorkItemResponse` contains `List<AdoWorkItemResponse>` without `List<AdoWorkItemResponse>` being separately registered in the existing `TwigJsonContext`.

**Configuration**: The GitHub repo URL needs to be known at build time. Embed via MSBuild property:
```xml
<TwigGitHubRepo>owner/repo</TwigGitHubRepo>
```
Injected as an assembly metadata attribute and read at runtime.

#### 6. Changelog System

**Responsibility**: Generate and display changelog from conventional commits.

**Design**:
- **Generation** (in release workflow): Use `git log` between tags with conventional commit parsing. Output to `CHANGELOG.md` in the repo and as GitHub Release body.
- **`twig changelog` command**: Fetches release notes from GitHub Releases API (same endpoint as self-update). Displays the body of the last N releases.
- **`twig upgrade` changelog delta**: When updating from v1.0.0 to v1.2.0, fetches all releases between those versions and displays combined changelog.

**Changelog format**:
```markdown
## v1.2.0 (2026-03-15)

### Features
- feat: add widget support (#42)

### Bug Fixes
- fix: handle null area path (#41)

### Breaking Changes
- breaking: rename config key (#40)
```

**Release workflow changelog generation script**:
```bash
CURRENT_TAG=${GITHUB_REF_NAME}
# Handle first release (no previous tag exists): use full history
PREV_TAG=$(git describe --tags --abbrev=0 HEAD^ 2>/dev/null || echo "")
if [ -z "$PREV_TAG" ]; then
  RANGE="$CURRENT_TAG"
else
  RANGE="${PREV_TAG}..${CURRENT_TAG}"
fi
git log ${RANGE} --pretty=format:"- %s (%h)" \
  | grep -E "^- (feat|fix|docs|chore|refactor|perf|test|ci|build|breaking)" \
  > release_notes.md
# Ensure file is non-empty (fallback message if no conventional commits found)
if [ ! -s release_notes.md ]; then
  echo "- Initial release" > release_notes.md
fi
```

**Note on `breaking:` prefix**: The Conventional Commits specification uses `feat!:` or a `BREAKING CHANGE:` footer for breaking changes. This project uses `breaking:` as a project-specific convention for grep simplicity (see DD-10). Contributor docs and CI tooling must be aligned on this choice.

#### 7. Path Management

- Install scripts create `~/.twig/bin/` and add to user PATH.
- `twig upgrade` replaces binary in `~/.twig/bin/` (or wherever the current exe lives, detected via `Environment.ProcessPath`).
- WinGet manifest: Stub section in this document only. Not implemented in this plan.

### Design Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| DD-1 | MinVer over Nerdbank.GitVersioning or GitVersion | Simplest option — one package, zero config files, reads git tags. Perfect fit for "zero manual version management" requirement. |
| DD-2 | Matrix build with native runners (not cross-compilation) | .NET Native AOT does not support cross-OS compilation. Each OS must build on its native runner. Cross-arch (x64→arm64) is supported on same OS but not needed given GitHub has ARM64 macOS runners. |
| DD-3 | Rename-then-replace for Windows self-update | Windows locks running executables for writes but allows renames. This is the standard pattern (used by Rust's `self-replace` crate, VS Code, etc.). No external helper process needed. |
| DD-4 | GitHub Releases API (unauthenticated) for update checks | Public API, no auth required, rate limit of 60 req/hr is sufficient for CLI update checks. No token management complexity. |
| DD-5 | Source-generated JSON for GitHub API DTOs | Required for AOT compatibility. The existing `TwigJsonContext` pattern is already established. |
| DD-6 | `git log` for changelog generation (not a dedicated tool) | Keeps dependencies minimal. Conventional commit parsing via grep/sed in shell is sufficient. A tool like `git-cliff` could be adopted later. |
| DD-7 | Embed repo owner/name as assembly metadata | Avoids hardcoding in source. Can be overridden per fork. Single source of truth in MSBuild. |
| DD-8 | `v` prefix for version tags | Industry convention. MinVer supports this via `MinVerTagPrefix`. |
| DD-9 | Self-update command named `twig upgrade` (not `twig update`) | The existing `TwigCommands.Update` method (Program.cs line 295–296) already routes `twig update <field> <value>` for work item field updates via `UpdateCommand.cs`. Adding a second `Update` method would cause a C# compilation error. `upgrade` is semantically clear (compare: `brew upgrade`, `apt upgrade`) and avoids any method name collision. |
| DD-10 | Conventional commit prefix `breaking:` treated as project convention | The Conventional Commits spec uses `feat!:` or a `BREAKING CHANGE:` footer for breaking changes, not `breaking:` as a prefix. This plan uses `breaking:` as a project-specific convention for simplicity. The CI changelog script, contributor docs, and commit guidelines must all align on this divergence from the spec. |
| DD-11 | Explicit `<AssemblyName>twig</AssemblyName>` in `Twig.csproj` | The .NET SDK derives assembly name from the project file name, producing `Twig.exe` / `Twig` (capital T). Install scripts reference lowercase `twig` / `twig.exe`. On case-sensitive Linux/macOS, `chmod +x ~/.twig/bin/twig` would fail because the extracted binary is `Twig`. Adding explicit `<AssemblyName>twig</AssemblyName>` ensures lowercase binary names across all platforms. |
| DD-12 | Minimal numeric SemVer comparison (no third-party library) | `System.Version` cannot parse MinVer pre-release suffixes (e.g., `1.0.1-alpha.0.3` throws `FormatException`). A third-party SemVer library introduces AOT-compatibility risk. Instead, implement a ~20-line static `SemVerComparer` that: (1) strips the `v` prefix from tag names, (2) splits on `-` to separate `major.minor.patch` from pre-release suffix, (3) parses and compares `major.minor.patch` numerically via `System.Version`, (4) if numeric parts are equal, a version with a pre-release suffix is less than one without (per SemVer §11: `1.0.0-alpha < 1.0.0`). Pre-release-to-pre-release ordering is not needed — `twig upgrade` only upgrades to release versions. This is AOT-safe, dependency-free, and handles all practical cases. |

---

## Alternatives Considered

| Alternative | Pros | Cons | Decision |
|-------------|------|------|----------|
| Nerdbank.GitVersioning | More features, widely used | Requires `version.json`, complex height calculation, overkill for CLI tool | Rejected — MinVer is simpler |
| GitVersion | Branching-strategy-aware | YAML config, slower, opinionated about branching | Rejected — adds complexity for no gain |
| Cross-compilation via PublishAotCross | Fewer runners needed | Experimental, limited community support, fragile native toolchain deps | Rejected — native runners are reliable |
| `git-cliff` for changelog | Polished output, config-driven | Extra dependency (Rust binary), needs install step in CI | Rejected for now — shell script sufficient, can adopt later |
| dotnet global tool for distribution | Standard .NET distribution | Requires .NET runtime, defeats Native AOT purpose | Rejected — we distribute raw binaries |
| Helper process for Windows self-update | Simpler logic | Extra binary to distribute, more failure modes | Rejected — rename trick is proven |

---

## Dependencies

### External Dependencies

| Dependency | Version | Purpose |
|------------|---------|---------|
| MinVer (NuGet) | 7.0.0 | Automatic semver from git tags |
| GitHub Actions `actions/checkout@v4` | v4 | Repository checkout with full history |
| GitHub Actions `actions/setup-dotnet@v4` | v4 | .NET SDK setup |
| GitHub Actions `actions/upload-artifact` / `actions/download-artifact` | v4 | Artifact transfer between jobs |
| GitHub Actions `softprops/action-gh-release@v2` | v2 | Create GitHub Release with assets |
| GitHub Releases API | v3 REST | Self-update checks and downloads |

### Internal Dependencies

| Component | Depends On | Notes |
|-----------|-----------|-------|
| Release workflow | MinVer integration | Must have versioning working first |
| Install scripts | Release workflow | Need releases to exist before install can download them |
| `twig upgrade` | Release workflow | Needs published releases |
| `twig changelog` | Release workflow | Needs release notes in GitHub Releases |

### Sequencing Constraints

1. MinVer integration must be completed first (all other components depend on correct versioning).
2. CI workflow should be set up before release workflow (validate the build process).
3. Release workflow must produce at least one release before install scripts and self-update can be tested end-to-end.

---

## Impact Analysis

### Components Affected

| Component | Impact |
|-----------|--------|
| `Directory.Build.props` | Remove `Version`, add MinVer reference |
| `Directory.Packages.props` | Add MinVer package version |
| `Program.cs` (`VersionHelper`) | Update fallback from `"0.1.0"` to `"0.0.0"` — after MinVer, this is dead code but should not contain a plausible version string |
| `TwigCommands` | Add `Upgrade` and `Changelog` command routing. **Note**: The existing `Update` method (line 295–296) routes `twig update <field> <value>` for work item field updates and is **not** modified. The self-update command uses the distinct name `Upgrade` to avoid a C# method name collision (see DD-9). |
| `src/Twig/Commands/UpdateCommand.cs` | **No changes needed** — existing work item field update command is unaffected. Listed here to document that the naming collision was evaluated and resolved (see DD-9). |
| `TwigJsonContext` | Add GitHub API DTOs |
| `Program.cs` (DI) | Register new services (IGitHubReleaseService, SelfUpdateCommand, ChangelogCommand) |

### Backward Compatibility

- **Version string format**: Changes from static `"0.1.0"` to MinVer-derived (e.g., `"0.1.0"` on tag, `"0.1.1-alpha.0.3"` between tags). `VersionHelper.GetVersion()` already strips `+` metadata, so output format is compatible.
- **CLI commands**: New commands (`upgrade`, `changelog`) are additive. The existing `twig update <field> <value>` command is unchanged. No existing commands change.
- **Binary name**: `Twig.csproj` has no explicit `<AssemblyName>`, so .NET SDK defaults to `Twig` (capital T). This plan adds `<AssemblyName>twig</AssemblyName>` to produce lowercase `twig` / `twig.exe`, matching install script expectations and avoiding case-sensitivity failures on Linux/macOS (see DD-11).

### Performance Implications

- **Build time**: Native AOT publish adds ~2-5 minutes per platform. Matrix parallelism keeps total wall-clock time under 15 minutes.
- **Self-update**: Downloads 10-30MB binary. GitHub CDN is fast. No impact on normal CLI operations.

---

## Security Considerations

- **Install scripts via pipe-to-shell**: This is inherently risky (supply chain attack vector). Mitigation: host scripts in the repo itself (content is auditable), use HTTPS only, scripts verify downloaded binary size.
- **GitHub API (unauthenticated)**: Rate-limited to 60 req/hr per IP. No sensitive data exposed. Only reads public release metadata.
- **Self-update integrity**: In the initial implementation, we trust HTTPS transport security. Future enhancement: checksum verification (SHA256 in release assets). This is explicitly a future item, not a blocker.
- **No secrets in CI**: Release workflow uses `GITHUB_TOKEN` (automatically provided by GitHub Actions). No additional secrets needed.
- **Install path**: `~/.twig/bin/` is user-writable. No elevated permissions required.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Native AOT build fails on GitHub runners (missing native toolchain) | Medium | High | Use official `setup-dotnet` action; pin SDK version via `global.json`; test on all runner types early |
| SQLite native library not bundled correctly for non-Windows RIDs | Medium | High | `SQLitePCLRaw.bundle_e_sqlite3` handles this; verify binary size and `ldd`/`otool` output in CI |
| GitHub API rate limiting blocks self-update checks | Low | Low | 60 req/hr is generous for CLI tool; cache last-check timestamp; only check once per session |
| Windows rename-trick fails on some antivirus configurations | Low | Medium | Document workaround (manual download); consider retry logic |
| macOS Gatekeeper blocks unsigned binary | Medium | Medium | Document `xattr -d com.apple.quarantine` workaround; code signing is a future item |
| `global.json` SDK 10.0.104 not available on GitHub runners | Low | High | Use `setup-dotnet` to install exact version; `rollForward: latestMinor` provides fallback |
| Release workflow fails with 403 due to insufficient `GITHUB_TOKEN` permissions | Medium | High | Explicitly set `permissions: contents: write` in `release.yml` (see ITEM-009). Default token permissions are read-only on many repos since GitHub's 2022 policy change. |
| First-ever release fails during changelog generation (no previous tag) | High | Medium | Changelog script uses `git describe` fallback — if no previous tag exists, logs full history up to current tag (see Section 6 changelog script). |

---

## Open Questions

| ID | Question | Impact | Owner | Status |
|----|----------|--------|-------|--------|
| OQ-1 | What is the GitHub repository `{owner}/{repo}` name? | Needed for install script URLs and self-update API endpoint | Project owner | **Resolved**: `PolyphonyRequiem/twig` |
| OQ-2 | Should `twig upgrade` prompt for confirmation before downloading? | UX decision — silent vs. interactive | Project owner | **Resolved**: No prompt (auto-upgrade). User explicitly invoked `upgrade`. Add `--check` flag for peek-only. |
| OQ-3 | Should checksums (SHA256) be generated and verified during install/update? | Security hardening — adds complexity | Project owner | **Resolved**: Defer. Trust HTTPS transport security. Layer checksums/signing later. |
| OQ-4 | Should the release workflow commit `CHANGELOG.md` back to the repo? | Changelog-in-repo vs. GitHub-Release-only | Project owner | **Resolved**: GitHub Release only. `twig changelog` fetches from API. No commit-back complexity. |
| OQ-5 | What is the minimum macOS version to support? (affects runner choice for osx-x64) | `macos-13` (Intel) vs `macos-14` (ARM) | Project owner | **Resolved**: Keep all 4 RIDs. `macos-13` for osx-x64, `macos-latest` for osx-arm64. Drop osx-x64 when GitHub deprecates macos-13 runners. |
| OQ-6 | Should `twig upgrade --pre` allow updating to pre-release versions? | Feature scope | Project owner | **Resolved**: No. Defer `--pre` flag. Pre-releases are for source builds only. |

---

## Implementation Phases

### Phase 1: Versioning Foundation
**Exit criteria**: `dotnet build` produces correct version from git tags; `twig --version` shows MinVer-derived version; no hardcoded version in source.

### Phase 2: CI Pipeline
**Exit criteria**: GitHub Actions CI workflow validates build+test on every PR to `main`.

### Phase 3: Release Pipeline
**Exit criteria**: Pushing a `v*` tag triggers workflow that produces 4 platform binaries, creates GitHub Release with changelog.

### Phase 4: Install Scripts
**Exit criteria**: `install.ps1` and `install.sh` successfully download and install latest release; PATH is configured.

### Phase 5: Self-Update & Changelog Commands
**Exit criteria**: `twig upgrade` checks for and applies updates; `twig changelog` displays recent changes.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `.github/workflows/ci.yml` | CI pipeline — build + test on PR |
| `.github/workflows/release.yml` | Release pipeline — multi-platform Native AOT build + GitHub Release |
| `install.ps1` | Windows install script |
| `install.sh` | Linux/macOS install script |
| `src/Twig/Commands/SelfUpdateCommand.cs` | `twig upgrade` command implementation |
| `src/Twig/Commands/ChangelogCommand.cs` | `twig changelog` command implementation |
| `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | Interface for GitHub Releases API |
| `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | GitHub Releases API client implementation |
| `src/Twig.Infrastructure/GitHub/GitHubDtos.cs` | DTOs for GitHub API responses |
| `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | Platform-specific binary replacement logic |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `Directory.Build.props` | Remove `<Version>0.1.0</Version>`, add MinVer package reference with `PrivateAssets="All"` |
| `Directory.Packages.props` | Add `<PackageVersion Include="MinVer" Version="7.0.0" />` |
| `src/Twig/Program.cs` | Register `SelfUpdateCommand`, `ChangelogCommand`, `IGitHubReleaseService`; add `Upgrade`/`Changelog` routes to `TwigCommands` (existing `Update` route for work item field updates is untouched); add old-binary cleanup on startup |
| `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | Add `[JsonSerializable(typeof(GitHubRelease))]` and `[JsonSerializable(typeof(List<GitHubRelease>))]`. `List<GitHubRelease>` is required because the `/releases?per_page={count}` endpoint returns a raw JSON array (not a wrapper object) — without it, AOT deserialization throws `NotSupportedException`. `GitHubAsset` and `List<GitHubAsset>` do NOT need explicit registration — source generation automatically handles property types of already-registered types (consistent with how `AdoBatchWorkItemResponse` handles `List<AdoWorkItemResponse>`). |
| `src/Twig/Twig.csproj` | Add `<AssemblyName>twig</AssemblyName>` to produce lowercase binary names matching install script expectations (see DD-11); add `<TwigGitHubRepo>` property for embedding repo identifier |
| `.gitignore` | No changes needed |

### Deleted Files

| File Path | Reason |
|-----------|--------|
| (none) | |

---

## Implementation Plan

### EPIC-001: MinVer Versioning Integration

**Goal**: Replace hardcoded version with automatic git-tag-based versioning via MinVer.

**Prerequisites**: None.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-001 | IMPL | Add `MinVer` v7.0.0 to `Directory.Packages.props` as a centrally managed package version | `Directory.Packages.props` | DONE |
| ITEM-002 | IMPL | Remove `<Version>0.1.0</Version>` from `Directory.Build.props`; add `<PackageReference Include="MinVer" PrivateAssets="All" />` to `Directory.Build.props`; set `<MinVerTagPrefix>v</MinVerTagPrefix>` | `Directory.Build.props` | DONE |
| ITEM-002a | IMPL | Update `VersionHelper.GetVersion()` fallback in `Program.cs` from `"0.1.0"` to `"0.0.0"`. After MinVer integration, the `AssemblyInformationalVersionAttribute` is always set, so this fallback is effectively dead code. Changing it to `"0.0.0"` avoids confusion if MinVer is ever misconfigured — the hardcoded `"0.1.0"` could be mistaken for a valid version. | `src/Twig/Program.cs` | DONE |
| ITEM-002b | IMPL | Add `<AssemblyName>twig</AssemblyName>` to `src/Twig/Twig.csproj` to produce lowercase binary names (`twig` / `twig.exe`). Without this, the .NET SDK derives the assembly name from the project file name, producing `Twig.exe` (Windows) and `Twig` (Linux/macOS). Install scripts reference lowercase `twig`; on case-sensitive Linux/macOS, `chmod +x ~/.twig/bin/twig` would fail because tar extraction produces `Twig`. See DD-11. | `src/Twig/Twig.csproj` | DONE |
| ITEM-003 | TEST | Verify `dotnet build` produces version from git tag: create a local tag `v0.2.0`, build, check `AssemblyInformationalVersionAttribute`. Verify `VersionHelper.GetVersion()` returns `"0.2.0"`. | (manual verification) | DONE |
| ITEM-004 | TEST | Verify version falls back to `0.0.0-alpha.0.{height}` when no tags exist (clean clone). Verify `--version` flag still works. | (manual verification) | DONE |

**Acceptance Criteria**:
- [x] No hardcoded version string in any `.props`, `.csproj`, or `.cs` file (including `VersionHelper` fallback updated from `"0.1.0"` to `"0.0.0"`)
- [x] `<AssemblyName>twig</AssemblyName>` is present in `Twig.csproj`, producing lowercase binary names on all platforms
- [x] `dotnet build` succeeds and assembly version matches latest git tag
- [x] `twig --version` outputs MinVer-derived version
- [x] `twig version` subcommand outputs MinVer-derived version

---

### EPIC-002: CI Workflow

**Goal**: Create GitHub Actions workflow that validates builds and tests on every PR.

**Prerequisites**: EPIC-001 (MinVer must be integrated so version resolves during CI build).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-005 | IMPL | Create `.github/workflows/ci.yml` with: trigger on `pull_request` and `push` to `main`; job `build-and-test` on `ubuntu-latest`; steps: checkout (fetch-depth: 0, filter: tree:0), setup-dotnet (from global.json), dotnet restore, dotnet build --no-restore, dotnet test --no-build --settings test.runsettings | `.github/workflows/ci.yml` | DONE |
| ITEM-006 | TEST | Push a test branch, open PR, verify CI runs successfully | (manual verification) | DONE |

**Acceptance Criteria**:
- [x] CI workflow triggers on pull_request to main
- [x] CI workflow triggers on push to main
- [x] Build and all tests pass on ubuntu-latest
- [x] MinVer resolves version correctly in CI (fetch-depth: 0)

---

### EPIC-003: Release Workflow

**Goal**: Automate multi-platform Native AOT builds and GitHub Release creation on tag push.

**Prerequisites**: EPIC-001, EPIC-002.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-007 | IMPL | Create `.github/workflows/release.yml` with tag trigger (`push: tags: ['v*']`). Define matrix strategy with 4 entries: `{rid: win-x64, os: windows-latest, archive: zip}`, `{rid: linux-x64, os: ubuntu-latest, archive: tar.gz}`, `{rid: osx-x64, os: macos-13, archive: tar.gz}`, `{rid: osx-arm64, os: macos-latest, archive: tar.gz}`. | `.github/workflows/release.yml` | TO DO |
| ITEM-008 | IMPL | In `build` job: checkout (fetch-depth: 0), setup-dotnet, `dotnet restore`, `dotnet test --settings test.runsettings`, `dotnet publish src/Twig/Twig.csproj -c Release -r ${{ matrix.rid }} --self-contained true -o ./publish/${{ matrix.rid }}`. Archive: zip on Windows, tar.gz on Linux/macOS. Upload artifact. | `.github/workflows/release.yml` | TO DO |
| ITEM-009 | IMPL | In `release` job (needs: build): download all artifacts, generate changelog via `git log` between previous tag and current tag filtering conventional commit prefixes (with first-release fallback — see Section 6 changelog script), create GitHub Release via `softprops/action-gh-release@v2` with release notes body and all 4 archive files attached. **Must** set `permissions: contents: write` on the workflow or job. | `.github/workflows/release.yml` | TO DO |
| ITEM-010 | IMPL | Add changelog generation step: extract version from tag, find previous tag via `git describe --tags --abbrev=0 HEAD^ 2>/dev/null` with fallback to full history when no previous tag exists (first release), generate markdown-formatted changelog grouping by conventional commit type (feat/fix/docs/chore/breaking). | `.github/workflows/release.yml` | TO DO |
| ITEM-011 | TEST | Tag `v0.2.0-rc.1`, push, verify: all 4 platform builds succeed, GitHub Release is created with correct assets and changelog. | (manual verification) | TO DO |

**Acceptance Criteria**:
- [ ] Pushing `v*` tag triggers release workflow
- [ ] Native AOT binaries are built for all 4 RIDs (win-x64, linux-x64, osx-x64, osx-arm64)
- [ ] Tests pass before publish on each platform
- [ ] Archives are correctly named: `twig-win-x64.zip`, `twig-linux-x64.tar.gz`, `twig-osx-x64.tar.gz`, `twig-osx-arm64.tar.gz`
- [ ] GitHub Release is created with auto-generated changelog from conventional commits
- [ ] All 4 archives are attached to the GitHub Release

---

### EPIC-004: Install Scripts

**Goal**: Create one-liner install scripts for Windows and Unix platforms.

**Prerequisites**: EPIC-003 (at least one release must exist).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-012 | IMPL | Create `install.ps1`: Query GitHub API for latest release, find `twig-win-x64.zip` asset, download to temp, create `~/.twig/bin/`, extract `twig.exe`, add `~/.twig/bin` to user PATH via `[Environment]::SetEnvironmentVariable(..., 'User')` if not present, print version. Handle errors (no internet, API failure). | `install.ps1` | TO DO |
| ITEM-013 | IMPL | Create `install.sh`: Detect OS (`uname -s` → Linux/Darwin) and arch (`uname -m` → x86_64/arm64), map to RID, query GitHub API for latest release, find matching asset, download with curl, create `~/.twig/bin/`, extract with tar, chmod +x, detect shell (bash/zsh/fish) and append `export PATH="$HOME/.twig/bin:$PATH"` to profile if not present, print version. | `install.sh` | TO DO |
| ITEM-014 | TEST | Test `install.ps1` on Windows: clean machine (no prior install), verify binary works and is in PATH. Test idempotency (run twice). | (manual verification) | TO DO |
| ITEM-015 | TEST | Test `install.sh` on Linux (x64) and macOS (arm64): clean machine, verify binary works and is in PATH. Test idempotency. | (manual verification) | TO DO |

**Acceptance Criteria**:
- [ ] `irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex` installs twig on Windows
- [ ] `curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash` installs twig on Linux/macOS
- [ ] `~/.twig/bin/` is created and contains the binary
- [ ] PATH is updated for the current user (persists across sessions)
- [ ] Running install a second time succeeds (idempotent)
- [ ] `twig --version` works after install

---

### EPIC-005: Self-Update Command

**Goal**: Add `twig upgrade` command that checks for and applies updates from GitHub Releases.

**Prerequisites**: EPIC-001, EPIC-003.

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-016 | IMPL | Create `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` with methods: `Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken)` and `Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(int count, CancellationToken)`. Define `GitHubReleaseInfo` record in Domain (tag, name, body, assets list). | `src/Twig.Domain/Interfaces/IGitHubReleaseService.cs` | TO DO |
| ITEM-017 | IMPL | Create `src/Twig.Infrastructure/GitHub/GitHubDtos.cs` with `GitHubRelease` and `GitHubAsset` DTOs for JSON deserialization. **MUST** use `internal sealed class` (matching the established ADO DTO pattern — `AdoWorkItemResponse`, `AdoIterationResponse`, etc.) and **MUST** add explicit `[JsonPropertyName]` attributes on every property to handle the GitHub API's snake_case naming (e.g., `[JsonPropertyName("tag_name")]` on `TagName`, `[JsonPropertyName("browser_download_url")]` on `BrowserDownloadUrl`). The existing `TwigJsonContext` uses `CamelCase` naming policy — without explicit `[JsonPropertyName]`, `TagName` would deserialize from `tagName` (which doesn't exist in GitHub responses; the actual key is `tag_name`), causing both version comparison and download URL resolution to silently fail with empty strings. Add to `TwigJsonContext`: `[JsonSerializable(typeof(GitHubRelease))]` and `[JsonSerializable(typeof(List<GitHubRelease>))]`. **`List<GitHubRelease>` is required** because the `/releases?per_page={count}` endpoint returns a raw JSON array (not a wrapper object) — without this registration, the source-generated context has no metadata for the collection type, causing `NotSupportedException` at runtime in AOT mode (breaking `twig changelog` and the multi-version upgrade delta). `GitHubAsset` and `List<GitHubAsset>` do **NOT** need explicit registration — source generation automatically handles property types of already-registered types (consistent with how `AdoBatchWorkItemResponse` contains `List<AdoWorkItemResponse>` without separate registration). | `src/Twig.Infrastructure/GitHub/GitHubDtos.cs`, `src/Twig.Infrastructure/Serialization/TwigJsonContext.cs` | TO DO |
| ITEM-018 | IMPL | Create `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` implementing `IGitHubReleaseService`. Use `HttpClient` to call `https://api.github.com/repos/PolyphonyRequiem/twig/releases/latest` and `/releases?per_page={count}`. Set `User-Agent: twig-cli` header (required by GitHub API). Map DTOs to domain records. | `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs` | TO DO |
| ITEM-019 | IMPL | Create `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` with method `Task UpdateBinaryAsync(string downloadUrl, string archiveName, CancellationToken)`. Detect current exe path via `Environment.ProcessPath`. On Windows: rename current exe → `.old`, extract new exe. On Unix: extract directly, `chmod +x`. Return path to new binary. | `src/Twig.Infrastructure/GitHub/SelfUpdater.cs` | TO DO |
| ITEM-020 | IMPL | Create `src/Twig/Commands/SelfUpdateCommand.cs`. Logic: get current version, call `IGitHubReleaseService.GetLatestReleaseAsync()`, compare versions using a minimal AOT-safe `SemVerComparer` (see DD-12), determine RID from `RuntimeInformation`, find matching asset, download via `SelfUpdater`, display changelog delta (release body), print success message. **SemVer comparison strategy**: Implement a static `SemVerComparer.Compare(string a, string b)` method (~20 lines, no dependencies) that: (1) strips `v` prefix if present, (2) splits on `-` to separate `major.minor.patch` from pre-release suffix, (3) parses the numeric `major.minor.patch` portion via `System.Version` (which handles 3-part version strings), (4) if numeric parts are equal, a version WITH a pre-release suffix (e.g., `1.0.1-alpha.0.3`) is LESS THAN one without (per SemVer §11). Pre-release-to-pre-release ordering is not needed — `twig upgrade` only offers upgrade to release versions (GitHub Release tags). This avoids `FormatException` from `System.Version.Parse("1.0.1-alpha.0.3")` and avoids any third-party SemVer library with unknown AOT compatibility. | `src/Twig/Commands/SelfUpdateCommand.cs` | TO DO |
| ITEM-021 | IMPL | Add `<TwigGitHubRepo>` MSBuild property to `src/Twig/Twig.csproj` (default to placeholder, overridable). Generate `AssemblyMetadataAttribute` with key `"GitHubRepo"`. Read in `SelfUpdateCommand` at runtime. | `src/Twig/Twig.csproj` | TO DO |
| ITEM-022 | IMPL | Register `IGitHubReleaseService`, `SelfUpdater`, `SelfUpdateCommand` in DI (`Program.cs`). Add `Upgrade` route to `TwigCommands` (note: `Update` route is already occupied by `twig update <field> <value>` for work item field updates — see DD-9). Add startup cleanup: if `twig.old.exe` exists next to current exe, delete it. | `src/Twig/Program.cs` | TO DO |
| ITEM-023 | IMPL | Implement RID detection utility: use `RuntimeInformation.RuntimeIdentifier` as the primary approach — in a Native AOT binary, this returns the compile-time target RID (e.g., `linux-x64`, `win-x64`, `osx-arm64`) directly, which is the exact string needed for asset name construction (e.g., `twig-linux-x64.tar.gz`). Fall back to manual OS/arch detection via `RuntimeInformation.OSDescription` and `RuntimeInformation.ProcessArchitecture` only if `RuntimeIdentifier` returns an empty or unrecognized value (defensive edge case). This is simpler and less error-prone than manual OS/arch inference, which must handle edge cases like `linux-musl-x64` vs `linux-x64`. | `src/Twig/Commands/SelfUpdateCommand.cs` | TO DO |
| ITEM-024 | TEST | Unit test `SemVerComparer` and `SelfUpdateCommand` version comparison logic: (a) numeric comparison — `1.0.0` < `1.1.0`, `1.2.0` > `1.1.0`, `1.0.0` == `1.0.0`; (b) pre-release handling — `1.0.1-alpha.0.3` < `1.0.1` (pre-release is less than release per SemVer §11); (c) `v` prefix stripping — `v1.0.0` == `1.0.0`; (d) upgrade decision — current < latest → update, current == latest → up to date, current > latest → up to date (dev build), pre-release current < matching release → update. Mock `IGitHubReleaseService`. | `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | TO DO |
| ITEM-025 | TEST | Unit test `GitHubReleaseClient` JSON deserialization with sample GitHub API response (snake_case keys). Verify DTOs map correctly — specifically verify that `tag_name` deserializes to `TagName` and `browser_download_url` deserializes to `BrowserDownloadUrl` (validates `[JsonPropertyName]` attributes are correct). | `tests/Twig.Infrastructure.Tests/GitHub/GitHubReleaseClientTests.cs` | TO DO |
| ITEM-026 | TEST | Unit test RID detection: verify `RuntimeInformation.RuntimeIdentifier` is used as primary source, verify correct asset name is selected for known RIDs (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`), and verify fallback to OS/arch detection for unrecognized or empty RIDs. | `tests/Twig.Cli.Tests/Commands/SelfUpdateCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig upgrade` checks GitHub Releases API for latest version
- [ ] If newer version available: downloads correct platform binary, replaces current binary, displays changelog
- [ ] If already up to date: displays "Already up to date (vX.Y.Z)"
- [ ] Windows file-lock handled via rename trick
- [ ] Unix update works via direct overwrite
- [ ] Old binary cleaned up on next launch (Windows)
- [ ] All code is AOT-compatible (source-generated JSON, no reflection)
- [ ] `SemVerComparer` correctly handles pre-release suffixes (e.g., `1.0.1-alpha.0.3` < `1.0.1`) without `System.Version.Parse` exceptions
- [ ] Unit tests cover version comparison (including pre-release), RID detection, and DTO deserialization

---

### EPIC-006: Changelog Command

**Goal**: Add `twig changelog` command to display recent release notes.

**Prerequisites**: EPIC-005 (shares `IGitHubReleaseService`).

| Task | Type | Description | Files | Status |
|------|------|-------------|-------|--------|
| ITEM-027 | IMPL | Create `src/Twig/Commands/ChangelogCommand.cs`. Calls `IGitHubReleaseService.GetReleasesAsync(count: 5)`. Formats each release as: version header, date, body (release notes). Supports `--count N` flag. | `src/Twig/Commands/ChangelogCommand.cs` | TO DO |
| ITEM-028 | IMPL | Add `Changelog` route to `TwigCommands` in `Program.cs`. Register `ChangelogCommand` in DI. | `src/Twig/Program.cs` | TO DO |
| ITEM-029 | TEST | Unit test `ChangelogCommand` formatting with mocked release data. | `tests/Twig.Cli.Tests/Commands/ChangelogCommandTests.cs` | TO DO |

**Acceptance Criteria**:
- [ ] `twig changelog` displays the last 5 releases with version, date, and notes
- [ ] `twig changelog --count 10` shows more releases
- [ ] Graceful error if no releases exist or API is unreachable
- [ ] Output is formatted for terminal readability

---

## References

| Resource | URL |
|----------|-----|
| MinVer — GitHub | https://github.com/adamralph/minver |
| MinVer — NuGet | https://www.nuget.org/packages/MinVer/ |
| .NET Native AOT deployment | https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ |
| .NET Native AOT cross-compilation | https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile |
| GitHub Actions — checkout with fetch-depth for MinVer | https://github.com/adamralph/minver#why-is-the-default-version-sometimes-used-in-github-actions |
| GitHub Releases API | https://docs.github.com/en/rest/releases/releases |
| softprops/action-gh-release | https://github.com/softprops/action-gh-release |
| Windows self-replace pattern | https://github.com/mitsuhiko/self-replace |
| Multiplatform AOT with SQLite guide | https://www.mostlylucid.net/blog/multiplatform-aot-sqlite |
| Conventional Commits specification | https://www.conventionalcommits.org/en/v1.0.0/ |
| SQLitePCLRaw.bundle_e_sqlite3 | https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3 |
