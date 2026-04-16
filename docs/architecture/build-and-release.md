# Build & Release

AOT compilation strategy, versioning, release pipeline, companion binaries,
and local development workflow.

---

## 1. AOT Compilation

### Configuration

All publishable projects target `net10.0` (set in `Directory.Build.props`).
The two main binaries — `twig` and `twig-mcp` — are AOT-compiled, with
AOT properties set in their respective `.csproj` files:

| Property | Value | Purpose |
|----------|-------|---------|
| `PublishAot` | `true` | Native ahead-of-time compilation |
| `PublishTrimmed` | `true` | IL trimming for size reduction |
| `TrimMode` | `full` | Aggressive — removes all unused code and metadata |
| `StripSymbols` | `true` | PDB symbols excluded from binary |
| `InvariantGlobalization` | `true` | No culture data (~20 MB saving) |
| `JsonSerializerIsReflectionEnabledByDefault` | `false` | Forces source-gen JSON |
| `IsAotCompatible` | `true` | Declares compatibility |

### Constraints & trade-offs

| Constraint | Impact | Resolution |
|------------|--------|------------|
| No reflection | No runtime type inspection | Source-generated JSON (`TwigJsonContext`), explicit service registration |
| No dynamic code gen | IL Emit impossible | ConsoleAppFramework source generators |
| No globalization data | English/UTC only | `InvariantGlobalization=true` |
| Trimming removes unused code | Internal types invisible to tests | `InternalsVisibleTo` attributes on every library |
| No `Assembly.LoadFrom` | No plugin loading | All assemblies statically linked |

### AOT-safe dependency stack

| Package | AOT-safe |
|---------|----------|
| ConsoleAppFramework | ✔ |
| ModelContextProtocol | ✔ |
| Microsoft.Data.Sqlite | ✔ |
| SQLitePCLRaw.bundle_e_sqlite3 | ✔ (native) |
| Spectre.Console | ✔ |
| Markdig | ✔ |
| Terminal.Gui v2 | ✘ (reflection) |

Terminal.Gui's reflection dependency is the reason `twig-tui` opts out of
AOT (see §4).

---

## 2. Versioning

### MinVer

Version numbers are derived from Git tags via **MinVer 7.0.0**.

```xml
<!-- src/Twig/Twig.csproj and src/Twig.Mcp/Twig.Mcp.csproj -->
<MinVerTagPrefix>v</MinVerTagPrefix>
```

| Scenario | Example version |
|----------|-----------------|
| On tag `v1.2.3` | `1.2.3` |
| 4 commits after `v1.2.3` | `1.2.4-alpha.4+{hash}` |
| No tags | `0.0.0-alpha.0+{hash}` |

MinVer injects the version as `AssemblyInformationalVersionAttribute` at
build time. The CLI's `--version` flag, the MCP server info, and install
scripts all read this attribute.

### Design philosophy

- **Tag-driven:** No version files to maintain; `git tag v1.3.0 && git push --tags`
  triggers a release.
- **Semantic versioning:** MAJOR.MINOR.PATCH with pre-release segments.
- **CI integration:** The release workflow reads `GITHUB_REF_NAME` to
  determine the version.

---

## 3. Release Pipeline

### CI workflow (`.github/workflows/ci.yml`)

Runs on pull requests to `main` and pushes to `main`:

```
Restore → Build → Test (ubuntu-latest)
```

Test filter: `Category!=Interactive & Category!=Integration` with a
300-second timeout via `test.runsettings`.

### Release workflow (`.github/workflows/release.yml`)

Triggered by pushing a tag matching `v*`.

#### Build stage — matrix

| Platform | Runner | RID | Archive |
|----------|--------|-----|---------|
| Windows x64 | `windows-latest` | `win-x64` | `.zip` |
| Linux x64 | `ubuntu-latest` | `linux-x64` | `.tar.gz` |
| macOS Intel | `macos-15` | `osx-x64` | `.tar.gz` |
| macOS ARM64 | `macos-latest` | `osx-arm64` | `.tar.gz` |

Each platform builds three artifacts:

1. **twig** (CLI, AOT) — `dotnet publish src/Twig/Twig.csproj -c Release -r {RID} --self-contained true`
2. **twig-mcp** (MCP server, AOT) — same flags
3. **twig-tui** (TUI, non-AOT) — adds `/p:PublishTrimmed=false /p:PublishAot=false`

Build steps:

```
Checkout (fetch-depth: 0, filter: tree:0)
  → Setup .NET (SDK from global.json: 10.0.104, rollForward: latestMinor)
  → Restore
  → Build
  → Test
  → Publish (3 binaries)
  → Archive (zip on Windows, tar.gz on Unix)
  → Upload artifacts
```

#### Release stage

Runs on `ubuntu-latest` after all platform builds succeed:

1. Download all platform archives.
2. Generate changelog from conventional commits between current and
   previous tag. Commit types: `feat`, `fix`, `docs`, `chore`, `refactor`,
   `perf`, `test`, `ci`, `build`, `breaking`.
3. Create GitHub release via `softprops/action-gh-release@v2` with the
   four platform archives and the generated release notes.

---

## 4. Companion Binaries

### Project dependency graph

```
Twig.Domain (lib)
  │
  ├─▶ Twig.Infrastructure (lib)
  │     ├ Markdig, Microsoft.Data.Sqlite
  │     └ InternalsVisibleTo: all consumers + test projects
  │
  ├─▶ Twig (exe) ─── CLI ─── AOT
  │     └ ConsoleAppFramework, Spectre.Console
  │
  ├─▶ Twig.Mcp (exe) ─── MCP server ─── AOT
  │     └ ModelContextProtocol, Microsoft.Extensions.Hosting
  │
  └─▶ Twig.Tui (exe) ─── TUI ─── non-AOT, SingleFile
        └ Terminal.Gui v2 (nightly)
```

### twig (CLI)

- Assembly name: `twig`
- Output: `twig.exe` / `twig` (native AOT binary, ~15–20 MB)
- Framework: ConsoleAppFramework (source-gen command routing)
- Rendering: Spectre.Console (rich terminal output)

### twig-mcp (MCP server)

- Assembly name: `twig-mcp`
- Output: `twig-mcp.exe` / `twig-mcp` (native AOT binary)
- Transport: stdio (JSON-RPC over stdin/stdout)
- Registered in `.vscode/mcp.json` for IDE integration

### twig-tui (Terminal UI)

- Assembly name: `twig-tui`
- Output: `twig-tui.exe` / `twig-tui` (self-contained single-file, larger)
- `IsAotCompatible=false` — Terminal.Gui v2 beta relies on reflection
- `PublishSingleFile=true`, `SelfContained=true` — bundles .NET runtime
- Uses Terminal.Gui v2 nightly (`2.0.0-develop.5185`)

### Shared libraries

`Twig.Domain` and `Twig.Infrastructure` are `IsPackable=false` class
libraries. Both use `InternalsVisibleTo` extensively to expose internal
types to test projects and consuming executables.

---

## 5. Local Development

### publish-local.ps1

Builds and deploys all three binaries to a repo-local `.local/bin/`
directory for rapid iteration without touching the release installation.

```powershell
./publish-local.ps1          # Build and deploy locally
./publish-local.ps1 -Restore # Remove shims, restore release binary
```

#### What it does

1. Creates `.local/bin/` in the repo root.
2. Publishes `twig`, `twig-mcp`, and `twig-tui` to `.local/bin/`.
3. Installs **shim scripts** in `~/.twig/bin/` that redirect to the local
   build when run from within the repo.

#### Shim mechanism

The shim (`twig.cmd`) walks up from the current directory to the repo root,
looking for `.local/bin/twig.exe`. If found, it runs the local build. If
not (e.g. running outside the repo), it falls back to the release binary
(`twig-core.exe`):

```batch
:loop
if exist "%dir%\.local\bin\twig.exe" (
    "%dir%\.local\bin\twig.exe" %*
    exit /b %ERRORLEVEL%
)
if exist "%dir%\.git" goto fallback
```

This works from any subdirectory and is compatible with Git worktrees
(each worktree has its own `.local/bin/` but shares `~/.twig/bin/`).

### install.ps1 (Windows)

One-line installer for end users:

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

1. Queries GitHub Releases API for the latest release (supports `$env:GITHUB_TOKEN`).
2. Downloads `twig-win-x64.zip` to `$env:TEMP`.
3. Extracts to `~/.twig/bin/` (creates if missing).
4. Adds `~/.twig/bin` to user `PATH` (persistent).
5. Verifies with `twig --version`.

### install.sh (Unix)

```bash
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

Detects OS and architecture (`uname -s`, `uname -m`) to select the correct
RID. Extracts to `~/.twig/bin/`, makes binaries executable, and appends
`PATH` export to the appropriate shell profile (bash, zsh, or fish).

---

## 6. Package Management

### Central package management

`Directory.Packages.props` enables `ManagePackageVersionsCentrally=true`.
All NuGet version pins live in this single file.

### Key dependencies

| Category | Package | Version |
|----------|---------|---------|
| Versioning | MinVer | 7.0.0 |
| CLI framework | ConsoleAppFramework | 5.7.13 |
| MCP protocol | ModelContextProtocol | 1.2.0 |
| Database | Microsoft.Data.Sqlite | 10.0.6 |
| DB native | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.11 |
| Markdown | Markdig | 1.1.2 |
| Console UI | Spectre.Console | 0.54.0 |
| Terminal UI | Terminal.Gui | 2.0.0-develop.5185 |
| DI | Microsoft.Extensions.DependencyInjection | 10.0.6 |
| Hosting | Microsoft.Extensions.Hosting | 10.0.6 |
| Test framework | xunit | 2.9.3 |
| Assertions | Shouldly | 4.3.0 |
| Mocking | NSubstitute | 5.3.0 |

### .NET SDK

`global.json` pins SDK **10.0.104** with `rollForward: latestMinor`.

---

## 7. Testing

### Test projects

| Project | Scope |
|---------|-------|
| `Twig.Cli.Tests` | CLI command integration |
| `Twig.Domain.Tests` | Business logic / domain services |
| `Twig.Infrastructure.Tests` | Data access, ADO client |
| `Twig.Mcp.Tests` | MCP tool contract tests |
| `Twig.Tui.Tests` | TUI interaction tests |
| `Twig.TestKit` | Shared test utilities |

### CI test settings (`test.runsettings`)

```xml
<TestCaseFilter>Category!=Interactive&Category!=Integration</TestCaseFilter>
<TestSessionTimeout>300000</TestSessionTimeout>
```

Interactive and integration tests are excluded in CI. A 5-minute timeout
kills hung tests to prevent pipeline stalls.

### Running locally

```bash
dotnet test                                         # All tests
dotnet test --settings test.runsettings              # CI-safe subset
dotnet test --filter "Category=Integration"          # Integration only
```
