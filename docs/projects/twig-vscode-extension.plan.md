# Twig VS Code Extension — Solution Design & Implementation Plan

**Epic:** #1263 — Twig VSCode Extension
> **Status**: 🔨 In Progress
**Author:** Copilot (Principal Software Architect)
**Created:** 2026-04-15
**Revised:** 2026-04-15

---

## Executive Summary

This plan proposes a VS Code extension that surfaces twig CLI functionality directly in the editor, eliminating terminal context switches during ADO-tracked development. The extension adopts a **CLI-as-backend** architecture: a TypeScript UI shell delegates all ADO operations to the existing `twig` binary via `--output json`, inheriting 3,383 test methods without reimplementing the ~26K LoC domain layer. Key features include a status bar showing active context (via file watcher on `.twig/prompt.json`), a TreeView for work item hierarchy, Command Palette integration for state transitions, and CodeLens annotations for `AB#` references.

---

## Background

### Current State

Twig is an AOT-compiled .NET 10 CLI for Azure DevOps work-item triage. AOT and trim
settings (`PublishAot=true`, `TrimMode=full`) are configured per-project in the executable
`.csproj` files (`src/Twig/Twig.csproj`, `src/Twig.Mcp/Twig.Mcp.csproj`), not in
`Directory.Build.props` (which handles `TargetFramework`, `LangVersion`, `Nullable`,
`ImplicitUsings`, `TreatWarningsAsErrors`, `MinVerTagPrefix`, and `RunSettingsFilePath`).
The architecture follows a clean
layered design:

- **Domain Layer** (Twig.Domain): Pure business logic with zero NuGet dependencies. Includes
  the `WorkItem` aggregate root (command-queue pattern), `ProcessConfiguration`, ~29 files
  in `ValueObjects/` (value objects plus utility classes such as `SlugHelper`, `StateResolver`,
  and `IconSet`), and 42 domain services (in `Services/`).
- **Infrastructure Layer** (Twig.Infrastructure): ADO REST API client (anti-corruption layer),
  SQLite cache (WAL mode), authentication (AzCli + PAT), configuration
  (`.twig/config`), and telemetry.
- **Presentation Layer** (Twig CLI): 74 commands (including hidden backward-compat aliases,
  group prefixes, and the `help` pseudo-command) registered via ConsoleAppFramework with
  factory-based DI (AOT-safe). Output formatters: human (Spectre.Console), JSON, JSON-compact,
  and minimal.
- **MCP Server** (Twig.Mcp): Separate executable exposing domain services via stdio JSON-RPC.
  Currently placeholder tool stubs (ContextTools, ReadTools, MutationTools).
- **TUI** (Twig.Tui): Interactive terminal tree navigator using Terminal.Gui v2.

The codebase includes **3,383 test methods** across five test projects (Twig.Cli.Tests: 1,741,
Twig.Domain.Tests: 905, Twig.Infrastructure.Tests: 675, Twig.Tui.Tests: 57,
Twig.Mcp.Tests: 5). These expand to ~3,900 test cases when including parameterized
`[InlineData]` variants (exact count varies slightly as multi-parameter `[Theory]` methods
expand combinatorially).

### Prior Art: prompt.json

The CLI already produces a `.twig/prompt.json` file (written by `PromptStateWriter`) after
every mutation. This file contains:

```json
{
  "text": "📋 #1234 Feature Title [Active] •",
  "id": 1234,
  "type": "Task",
  "typeBadge": "📋",
  "title": "Feature Title",
  "state": "Active",
  "stateCategory": "InProgress",
  "isDirty": true,
  "typeColor": "#F2CB1D",
  "typeTextColor": "#000000",
  "stateColor": "#007ACC",
  "branch": "feature/1234-feature-title",
  "generatedAt": "2026-04-15T16:00:00.0000000Z"
}
```

This is the **zero-cost status signal** — the extension can watch this file with
`vscode.workspace.createFileSystemWatcher` and update the status bar without spawning
any process. The CLI updates it atomically (write to `.tmp` + `File.Move`).

### Prior Art: JSON Output Contract

All CLI commands support `--output json` which produces stable-schema JSON via manual
`Utf8JsonWriter` calls (AOT-compatible, see `src/Twig/Formatters/JsonOutputFormatter.cs`).
Key schemas:

- **Work Item** (`FormatWorkItem`): `{ id, title, type, state, assignedTo, areaPath, iterationPath, isDirty, isSeed, parentId, parent?: {id,title,type}, children?: [{id,title,type,state}], links?: [{sourceId,targetId,linkType}] }`
- **Tree** (`FormatTree`): `{ focus: {<WorkItem>}, parentChain: [{<WorkItem>}], children: [{<WorkItem>}], totalChildren: <int>, links: [{sourceId, targetId, linkType}] }`
- **Workspace** (`FormatWorkspace`): `{ context: {<WorkItem>}|null, sprintItems: [{<WorkItem>}], seeds: [{<WorkItem>}], staleSeeds: [<int>], dirtyCount: <int> }`
- **Query Results**: `{ items: [...], totalCount }`
- **Version** (`twig version` / `twig --version`): bare string via `Console.WriteLine`, e.g. `1.2.3` — plain text, not JSON. No quotes, no envelope. The extension must parse this with `stdout.trim()`, not `JSON.parse()`.

### Prior Art: MCP Server

The `Twig.Mcp` project demonstrates the pattern of reusing domain services from a
non-CLI context. It registers a subset of services (ActiveItemResolver, SyncCoordinator,
StatusOrchestrator, etc.) without CLI-specific dependencies. The VS Code extension follows
the same philosophy but via subprocess invocation rather than in-process hosting.

---

## Problem Statement

Developers using twig must constantly switch between their editor and terminal to:

1. **Check context** — "What work item am I on?" requires `twig status` in a terminal
2. **Navigate hierarchy** — "What are the child tasks?" requires `twig tree` in a terminal
3. **Transition state** — "Mark this Done" requires `twig state Done` in a terminal
4. **Set context** — "Switch to work item #1234" requires `twig set 1234` in a terminal
5. **Understand commits** — `AB#1234` references in git history have no inline resolution

Each context switch takes 5-15 seconds and breaks flow. Over a day of 50+ transitions,
this compounds into significant productivity loss. The VS Code extension eliminates these
context switches by embedding twig's functionality in the editor's native UI.

---

## Goals and Non-Goals

### Goals

1. **G-1**: Show active work item context in VS Code status bar, updated in real-time via
   `prompt.json` file watcher (zero process spawn for status updates)
2. **G-2**: Provide a TreeView showing work item hierarchy (parent chain + children) with
   expand/collapse, click-to-set-context, and state badges
3. **G-3**: Expose core twig commands via Command Palette: set, state, sync,
   refreshTree (4 commands covering the primary context-switching problem;
   workspace, note, new, and seed new are deferred per NG-4/NG-7)
4. **G-4**: Provide CodeLens annotations on `AB#` references in commit messages and source
   comments, showing work item title and state inline
5. **G-5**: Support workspace detection — auto-detect `.twig/config` in workspace folders
   and activate extension only when present
6. **G-6**: Publish as a VSIX package installable from marketplace or local file

### Non-Goals

- **NG-1**: Reimplementing twig domain logic in TypeScript — the extension is a UI shell
- **NG-2**: Real-time ADO webhook integration — the extension uses twig's cache model
- **NG-3**: Full work item editing (description, rich text fields) — complex editor UI
  deferred to later phases
- **NG-4**: Seed management UI — seeds are a power-user workflow better suited to terminal
- **NG-5**: Interactive conflict resolution — terminal-based workflow retained
- **NG-6**: Support for VS Code Web (codespaces) — requires WASM backend, deferred
- **NG-7**: Add Note and Open in Browser commands — not part of the core context-switching
  problem (set context, state transitions). Deferred to a later phase.

---

## Requirements

### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1 | Status bar shows active work item (type badge, ID, title, state, dirty indicator) | P0 |
| FR-2 | Status bar updates within 500ms of prompt.json change (file watcher) | P0 |
| FR-3 | TreeView shows work item hierarchy from `twig tree --output json` | P0 |
| FR-4 | TreeView nodes show type badge, ID, title, state with color coding | P0 |
| FR-5 | Click TreeView node → `twig set <id>` → updates context | P0 |
| FR-6 | Command palette: "Twig: Set Work Item" with ID input or fuzzy search | P0 |
| FR-7 | Command palette: "Twig: Change State" with state picker (populated via `twig states`) | P0 |
| FR-8 | Command palette: "Twig: Sync" to push/pull changes | P0 |
| FR-10 | CodeLens on `AB#<id>` patterns in files showing work item title + state | P1 |
| FR-11 | Workspace auto-detection: activate when `.twig/config` exists in any workspace folder | P0 |
| FR-12 | Configuration: `twig.cliPath` setting to override CLI binary location | P0 |
| FR-13 | Error handling: show notification when CLI is not found or workspace not initialized | P0 |
| FR-14 | TreeView context menu: Change State (only) | P1 |

> **Note:** FR-9 (Command palette: "Twig: Add Note") was evaluated and deferred to post-v1. See NG-7.

### Non-Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| NFR-1 | Extension activation time < 200ms (lazy activation on `.twig/config` presence) | P0 |
| NFR-2 | Status bar update latency < 500ms from file change | P0 |
| NFR-3 | CLI subprocess timeout: 15s (hardcoded) | P0 |
| NFR-4 | Extension bundle size < 500KB (TypeScript only, no native deps) | P0 |
| NFR-5 | Cross-platform: Windows, macOS, Linux (matches twig CLI platforms) | P0 |
| NFR-6 | VS Code minimum version: 1.85.0 (for latest TreeView API) — hard compatibility boundary | P0 |
| NFR-7 | No telemetry from extension itself (twig CLI has its own telemetry) | P0 |

---

## Proposed Design

### Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    VS Code Extension                     │
│                                                          │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌─────────┐ │
│  │ Status   │  │ TreeView │  │ Command  │  │CodeLens │ │
│  │ Bar      │  │ Provider │  │ Palette  │  │Provider │ │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  └────┬────┘ │
│       │              │              │              │      │
│  ┌────▼──────────────▼──────────────▼──────────────▼────┐│
│  │                TwigCliClient                         ││
│  │  - exec(command, args) → JSON                       ││
│  │  - showBatch(ids) → batched resolution              ││
│  │  - caches CLI path discovery                        ││
│  │  - timeout + error handling                         ││
│  └──────────────────┬──────────────────────────────────┘│
│                     │                                    │
│  ┌──────────────────▼──────────────────────────────────┐│
│  │           PromptStateWatcher                        ││
│  │  - FileSystemWatcher on .twig/prompt.json           ││
│  │  - Emits onStateChanged events                      ││
│  │  - Debounced reads (100ms)                          ││
│  └─────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
                        │
            ┌───────────▼───────────┐
            │     twig CLI binary   │
            │  (AOT native binary)  │
            │                       │
            │  --output json        │
            │  --output json-compact│
            └───────────────────────┘
```

### Key Components

#### 1. TwigCliClient (`src/twig-client.ts`)

Central service for all CLI interactions. Wraps `child_process.execFile` with:

- **CLI discovery**: Searches `twig.cliPath` setting → `PATH` → `~/.twig/bin/twig`
- **JSON parsing**: All commands invoked with `--output json`, responses parsed to typed interfaces
- **Timeout**: Default 15s (hardcoded)
- **Error mapping**: Maps exit codes to VS Code notifications:
  - Exit 1 + "ADO unreachable" → warning notification with retry action
  - Exit 1 + "not initialized" → prompt to run `twig init`
  - **Cancellation** → silent. Detected broadly: exit code 130 (Unix SIGINT convention)
    OR any non-zero exit with `OperationCanceledException` on stderr OR process killed by
    signal. Note: on Windows, Ctrl+C does not produce exit code 130 — the process may
    terminate with exit code 0xC000013A or similar. The extension must treat any of these
    as a silent cancellation rather than hardcoding exit code 130.
- **Working directory**: Uses first workspace folder containing `.twig/config`
- **Concurrency**: Serial FIFO command queue to avoid SQLite contention (WAL allows concurrent reads but twig CLI holds exclusive writes during sync).
- **Timeout recovery**: When a CLI process exceeds the 15s timeout, Node's `execFile`
  kills it via `SIGTERM` (Unix) / `TerminateProcess` (Windows). The serial queue catches
  the timeout error, rejects the pending promise with a `TimeoutError`, logs a warning via
  the output channel, and dequeues — proceeding to the next command. No manual process
  cleanup is needed because `execFile` with the `timeout` option handles termination.
  The queue itself is never blocked by a hung process.
- **Version check**: On activation, runs `twig --version`, parses bare version string (e.g.
  `1.2.3` — plain text, not JSON; parse with `stdout.trim()`, not `JSON.parse()`). If the
  CLI is below `MINIMUM_CLI_VERSION` (a constant set to `0.0.0` during development and updated
  to the current twig release version at extension release time — i.e., the release that ships
  `twig show --batch` and `twig states`), shows a blocking error notification:
  _"Twig extension requires twig CLI ≥ {version}. Please upgrade:
  https://github.com/dangreen-msft/twig/releases"_ and disables all extension functionality.

```typescript
interface TwigCliClient {
  exec(command: string, args?: string[]): Promise<TwigResult>;
  getTree(depth?: number): Promise<TreeData>;
  setState(state: string): Promise<void>;
  setWorkItem(idOrPattern: string): Promise<WorkItemStatus>;
  getStates(): Promise<StateInfo[]>;
  showBatch(ids: number[]): Promise<Map<number, WorkItemSummary>>;
  sync(): Promise<void>;
}
```

#### 2. PromptStateWatcher (`src/prompt-state-watcher.ts`)

File system watcher on `.twig/prompt.json` for zero-cost status updates:

- **FileSystemWatcher**: `vscode.workspace.createFileSystemWatcher('**/.twig/prompt.json')`
- **Debounce**: 100ms debounce on change events (atomic write may trigger multiple events)
- **Parse**: Reads JSON file, emits typed `PromptState` event
- **Fallback**: If file doesn't exist or is empty `{}`, emits "no context" state
- **Lifecycle**: Created on activation, disposed on deactivation
- **Multi-root workspace**: Uses first workspace folder containing `.twig/config`. Multi-root folder-change handling is deferred.

```typescript
interface PromptState {
  text: string;
  id: number;
  type: string;
  typeBadge: string;
  title: string;
  state: string;
  stateCategory: string;
  isDirty: boolean;
  typeColor: string | null;
  typeTextColor: string | null;
  stateColor: string | null;
  branch: string | null;
  generatedAt: string;
}
```

#### 3. StatusBarManager (`src/status-bar.ts`)

VS Code status bar item showing active work item:

- **Position**: Left side, priority 100 (near source control indicators)
- **Content**: `$(type-icon) #ID Title [State]` with dirty indicator
- **Color**: The VS Code `StatusBarItem.color` property is deprecated. Only
  `backgroundColor` is supported, and it accepts only `ThemeColor` values (e.g.
  `statusBarItem.warningBackground`, `statusBarItem.errorBackground`). The extension maps
  `stateCategory` from prompt.json to semantic `ThemeColor` values (e.g., `InProgress` →
  default, `Completed` → `statusBarItem.errorBackground` for emphasis, `Proposed` →
  `statusBarItem.warningBackground`) rather than using raw hex `stateColor` values directly
- **Click action**: Opens command palette with "Twig: Set Work Item"
- **Tooltip**: Full work item details (type, assignedTo, iteration, branch)
- **Updates**: Listens to PromptStateWatcher events
- **Empty state**: Shows `$(circle-slash) Twig: No context` when no active item

#### 4. WorkItemTreeProvider (`src/tree-provider.ts`)

VS Code TreeDataProvider for the Explorer sidebar:

- **View ID**: `twigWorkItems`
- **Root nodes**: Active item's parent chain (from `parentChain` array in tree JSON) rendered
  top-down, with the active item (from `focus` field) highlighted
- **Child nodes**: Lazy-loaded via `twig tree --output json` (uses `children` array;
  `totalChildren` field indicates if more children exist). Note: `twig tree` performs a
  best-effort `SyncLinksAsync` network call before returning JSON — this may add ~200-500ms
  latency on first call; failures are silently swallowed (see Data Flow § Tree View Load)
- **Node rendering**:
  - Icon: Colored circle matching type color from process config
  - Label: `#ID Title`
  - Description: `[State]` with state category color
  - Context value: Work item ID (for context menu commands)
- **Refresh**: Auto-refresh on PromptStateWatcher events (context change)
- **Actions**: Click → `twig set <id>`, context menu → state change

#### 5. CodeLensProvider (`src/codelens-provider.ts`)

CodeLens annotations for `AB#<id>` patterns:

- **Trigger**: Files matching `**/*.{md,txt,cs,ts,js,py,yaml,json}` and `git-commit`
  language ID (VS Code's SCM commit message editor)
- **Pattern**: `/AB#(\d+)/g` — matches Azure DevOps work item references
- **Resolution**: Batched via `twig show --batch <ids>` (new CLI command, see Issue 7) to
  avoid N sequential process spawns.
- **Display**: `AB#1234 — 📋 Task: Feature Title [Active]`
- **Click action**: `twig set <id>` to set context
- **Caching**: Simple Map-based TTL cache inlined in the provider (100 items, 5-minute TTL) —
  no separate module needed for a single-use cache
- **Debounce**: 500ms after document edit before re-scanning

#### 6. CommandRegistrar (`src/commands.ts`)

Registers all Command Palette commands:

| Command ID | Label | Implementation |
|------------|-------|----------------|
| `twig.setWorkItem` | Twig: Set Work Item | Input box → `twig set <input>` |
| `twig.changeState` | Twig: Change State | Quick pick (states from `twig states` command) → `twig state <pick>` |
| `twig.sync` | Twig: Sync | `twig sync` with progress notification |
| `twig.refreshTree` | Twig: Refresh Tree | Refresh tree view data |

### Data Flow

#### Status Bar Update (passive, zero-cost)

```
twig CLI command execution
  → PromptStateWriter writes .twig/prompt.json atomically
    → VS Code FileSystemWatcher fires onChange
      → PromptStateWatcher debounces (100ms), reads JSON
        → StatusBarManager updates status bar text + color
```

#### Tree View Load

```
User expands tree node (or context changes)
  → WorkItemTreeProvider.getChildren(nodeId)
    → TwigCliClient.exec("tree", ["--output", "json"])
      → twig CLI reads SQLite cache for parent chain and children, then performs a
        best-effort SyncLinksAsync call (network request to ADO for non-hierarchy links).
        This sync may add ~200-500ms latency on first call; failures are silently swallowed.
        Returns JSON:
        { focus: {…}, parentChain: [{…}], children: [{…}], totalChildren: N, links: [{…}] }
        → Parse JSON → create TreeItem[] with icons/labels
          → VS Code renders tree nodes
```

#### State Transition

```
User selects "Twig: Change State" from command palette
  → CommandRegistrar fetches available states
    → TwigCliClient.exec("states", ["--output", "json"])
      → Returns states with categories and colors for the active item's type
  → Show QuickPick with available states (icons per category)
    → User selects target state
      → TwigCliClient.exec("state", [selectedState])
        → CLI validates transition, applies change
          → prompt.json updated → status bar auto-refreshes
            → TreeView auto-refreshes
```

#### CodeLens Resolution

```
User opens file containing "AB#1234", "AB#5678", "AB#9012"
  → CodeLensProvider.provideCodeLenses scans document for /AB#(\d+)/g
    → Returns unresolved CodeLens at each match position
      → CodeLensProvider.resolveCodeLens called for visible lenses
        → Collect uncached IDs, batch-resolve via twig show --batch 1234,5678,9012
          → Single CLI invocation returns all three items
            → Cache results (5-min TTL), return CodeLens titles
```

### Design Decisions

**DD-1: CLI subprocess over MCP client.** The MCP server (`Twig.Mcp`) contains 3 empty
stub classes (`ContextTools`, `ReadTools`, `MutationTools`) with zero implemented tool
methods. Even when implemented, the CLI provides a richer command set (74 commands).
The extension can migrate to MCP later as a performance optimization without architectural
changes.

**DD-2: prompt.json file watcher for status.** The CLI already writes this file atomically
after every mutation. Watching it is zero-cost (OS-level inotify/FSEvents/ReadDirectoryChanges)
and avoids spawning a process just to check status. This is the same pattern used by the
Oh My Posh shell prompt segment.

**DD-3: Serial command queue.** SQLite WAL mode allows concurrent reads but twig CLI
commands may perform writes (state changes, sync). A serial queue prevents "database is
locked" errors. Recovers from process timeouts without stalling (see DD-10). Future
optimization: parallel reads with write serialization.

**DD-4: Inlined TTL cache for CodeLens.** Work item data changes infrequently relative to how
often CodeLens is resolved (every scroll/edit). A 5-minute TTL balances freshness with
performance. Cache is a simple Map inlined into `codelens-provider.ts` — no separate module
needed for a single-use, ~30-line utility. Cache is cleared on `twig sync` or context change.

**DD-5: Monorepo placement.** The extension lives in `ext/twig-vscode/` within the
existing twig repo. This enables coordinated versioning (MinVer tags apply to both CLI
and extension), shared CI, and documentation co-location. The extension has its own
`package.json` and build pipeline (esbuild for bundling).

**DD-6: Activation event.** The extension uses `workspaceContains:**/.twig/config` activation
event, ensuring zero overhead for non-twig workspaces. Combined with `onCommand:twig.*`
for manual activation.

**DD-7: Batched CodeLens resolution.** A new `twig show --batch <ids>` CLI command accepts
comma-separated IDs and returns a JSON array of work item summaries in a single process
invocation. This prevents the N+1 problem where 50 `AB#` references would otherwise spawn
50 sequential CLI processes (~100ms each = ~5s cold start).

**DD-8: State picker via `twig states` command.** A new `twig states --output json` CLI
command returns the available states (with categories and colors) for the active work item's
type from the local process configuration cache. This fills the data gap for FR-7 — without
it, the extension has no way to populate the state quick pick. The command reads from the
`IProcessTypeStore` and requires no ADO network call.

**DD-10: Timeout recovery without queue stalls.** CLI process hangs beyond the 15s timeout
are handled by Node's `execFile` kill behavior (`SIGTERM`/`TerminateProcess`). The serial
queue catches the resulting error, rejects the promise, and advances to the next entry.
This guarantees the queue never permanently stalls from a hung process.

---

## Alternatives Considered

| Approach | Pros | Cons |
|----------|------|------|
| **CLI subprocess** (chosen) | Zero domain reimplementation, inherits all 3,383 test methods, single binary distribution, cache coherent | Process spawn latency (~100ms AOT), no streaming |
| **Embedded WASM** | In-process, no spawn cost | .NET → WASM is immature for AOT+SQLite, massive bundle size, dual maintenance |
| **MCP client** | Typed JSON-RPC, long-lived process | MCP tools are placeholder stubs, additional process management |
| **Direct ADO REST** | No CLI dependency | Reimplements ~26K LoC, no cache, no conflict resolution, dual maintenance |

CLI subprocess wins on pragmatism: the extension is a thin UI shell over a proven backend.

---

## Dependencies

### External Dependencies

| Dependency | Purpose | Version |
|------------|---------|---------|
| `vscode` | VS Code Extension API | ^1.85.0 |
| `esbuild` | TypeScript bundler for VSIX | ^0.20.0 |
| `@vscode/vsce` | VSIX packaging tool | ^2.22.0 |
| `typescript` | Language | ^5.4.0 |
| `@types/vscode` | VS Code type definitions | ^1.85.0 |
| `@types/node` | Node.js type definitions | ^20.0.0 |
| `mocha` | Test framework (VS Code extension convention) | ^10.4.0 |
| `@types/mocha` | Mocha type definitions | ^10.0.0 |
| `@vscode/test-electron` | VS Code extension test runner | ^2.3.0 |
| `glob` | File pattern matching for test discovery | ^10.0.0 |

No runtime npm dependencies — the extension uses only VS Code built-in APIs and
Node.js `child_process`.

### Internal Dependencies

- **twig CLI binary**: Must be installed and on PATH (or configured via `twig.cliPath`).
  Minimum version: defined by `MINIMUM_CLI_VERSION` constant in `twig-client.ts`, set to
  `0.0.0` during development and updated at extension release time to the current twig CLI
  release version (the release that ships `twig show --batch` and `twig states` commands).
- **`.twig/config`**: Workspace must be initialized via `twig init`
- **`.twig/prompt.json`**: Written by CLI after mutations (status bar depends on this)

### Sequencing Constraints

Two new CLI commands are required before the extension can be fully functional:

1. **`twig show --batch`** (Issue 7, T-7.1 — see Issue 7 below) — batched work item lookup for CodeLens.
   Without this, CodeLens works but with degraded performance (N sequential spawns).
2. **`twig states`** (Issue 7, T-7.2 — see Issue 7 below) — available states for the active item's type.
   Without this, the "Change State" quick pick cannot be populated.

These CLI changes (Issue 7 — see ADO Work Item Structure below) should ship before or alongside PG-3 (Tree View & Commands) and PG-4
(CodeLens). The extension scaffold (PG-2) and status bar (PG-2) are independent.

---

## Impact Analysis

### Build & CI

- **New CI job**: The extension requires a separate CI job (`npm ci`, `npm run lint`,
  `npm test`, `npm run build`) in the existing GitHub Actions workflow. Estimated CI
  time impact: +45–90s per PR (Node.js install + esbuild + tests).
- **Release pipeline**: VSIX packaging adds a new release artifact alongside the existing
  platform-specific CLI binaries. The VSIX is platform-independent (no native code).

### MinVer Tagging

Two publishable artifacts will share the same MinVer-based version:
- **twig CLI** (existing): tagged via `v<semver>` on the repo
- **twig-vscode VSIX** (new): version sourced from the same tag, written into
  `package.json` at build time via a `scripts/set-version.mjs` step

This means CLI and extension versions are always in sync, which simplifies the version
compatibility check in the extension.

### Backward Compatibility

- The two new CLI commands (`twig show --batch`, `twig states`) are additive — they
  don't break existing CLI behavior
- The version check on activation is the single gating mechanism: if the CLI is below
  `MINIMUM_CLI_VERSION`, all extension functionality is blocked with an upgrade prompt.
  There are no degraded-mode code paths.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| CLI not on PATH | Medium | High | Auto-detect `~/.twig/bin/twig`, show actionable notification with install link |
| CLI JSON schema changes | Low | Medium | Version-check CLI output via `twig --version`, degrade gracefully for unknown fields |
| SQLite locking under concurrent CLI calls | Medium | Medium | Serial command queue, retry with backoff |
| Large tree (1000+ items) perf | Low | Medium | Lazy loading with depth-1 expansion, pagination in tree provider |
| Cross-platform path handling | Medium | Low | Use `path.join`, normalize separators, test on Windows/macOS/Linux |
| CLI below minimum version | Medium | Medium | Version check on activation blocks all functionality with upgrade prompt (see TwigCliClient § Version check) |
| CLI process hang beyond 15s timeout | Low | Medium | `execFile` timeout kills the process; serial queue catches error and advances (see DD-10) |

---

## Security Considerations

- **No credentials stored by extension**: Authentication is handled entirely by the twig
  CLI (AzCli tokens or PAT stored in `.twig/config`). The extension never touches credentials.
- **No network calls from extension**: All ADO communication goes through the CLI subprocess.
  The extension makes zero direct HTTP requests.
- **CLI path injection**: The `twig.cliPath` setting is user-controlled. The extension
  validates it exists and is executable before invoking. No shell interpolation — uses
  `execFile` (not `exec`) to prevent command injection.
- **File watcher scope**: Limited to `.twig/prompt.json` within the workspace. No
  arbitrary file access.

---

## Open Questions

All design questions have been resolved. See the Appendix for decision history.

---

## Files Affected

### New Files

| File Path | Purpose |
|-----------|---------|
| `ext/twig-vscode/package.json` | Extension manifest: contributes, activation events, configuration |
| `ext/twig-vscode/tsconfig.json` | TypeScript configuration |
| `ext/twig-vscode/.vscodeignore` | Files to exclude from VSIX |
| `ext/twig-vscode/esbuild.mjs` | esbuild bundler configuration |
| `ext/twig-vscode/src/extension.ts` | Extension entry point: activate/deactivate |
| `ext/twig-vscode/src/types.ts` | TypeScript interfaces for all twig CLI JSON schemas |
| `ext/twig-vscode/src/twig-client.ts` | CLI subprocess client with JSON parsing |
| `ext/twig-vscode/src/prompt-state-watcher.ts` | File watcher for prompt.json |
| `ext/twig-vscode/src/status-bar.ts` | Status bar item manager |
| `ext/twig-vscode/src/tree-provider.ts` | TreeDataProvider for work item hierarchy |
| `ext/twig-vscode/src/codelens-provider.ts` | CodeLens provider for AB# annotations (includes inlined TTL cache) |
| `ext/twig-vscode/src/commands.ts` | Command palette command registrar |
| `ext/twig-vscode/src/test/suite/twig-client.test.ts` | Unit tests for CLI client |
| `ext/twig-vscode/src/test/suite/prompt-state-watcher.test.ts` | Unit tests for file watcher |
| `ext/twig-vscode/src/test/suite/codelens-provider.test.ts` | Unit tests for CodeLens |
| `ext/twig-vscode/src/test/suite/tree-provider.test.ts` | Unit tests for tree data provider |
| `ext/twig-vscode/src/test/suite/index.ts` | Test runner entry point |
| `ext/twig-vscode/README.md` | Extension stub README |
| `ext/twig-vscode/.eslintrc.json` | ESLint configuration |
| `src/Twig/Commands/StatesCommand.cs` | New CLI command: `twig states --output json` |
| `tests/Twig.Cli.Tests/Commands/StatesCommandTests.cs` | Tests for states command |
| `tests/Twig.Cli.Tests/Commands/ShowBatchTests.cs` | Tests for show --batch flag |

### Modified Files

| File Path | Changes |
|-----------|---------|
| `src/Twig/Commands/ShowCommand.cs` | Add `--batch` flag: accept comma-separated IDs, return JSON array |
| `src/Twig/Program.cs` | Register `States` command and `Show --batch` overload in `TwigCommands` |
| `src/Twig/Formatters/JsonOutputFormatter.cs` | Add `FormatWorkItemBatch` method for batched show output |
| `.github/workflows/ci.yml` | Add job for extension: npm install, lint, test, build |
| `.github/workflows/release.yml` | Add VSIX packaging and release asset upload |
| `README.md` | Add section about VS Code extension |

### Deleted Files

None.

---

## ADO Work Item Structure

**Epic #1263: Twig VSCode Extension**

### Issue 1: Extension Scaffold & CLI Client

**Goal**: Create the VS Code extension project structure, build pipeline, and the core
`TwigCliClient` that all other features depend on.

**Prerequisites**: None (first Issue)

**Satisfies**: FR-11, FR-12, FR-13, NFR-1, NFR-4, NFR-5

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-1.1 | Initialize extension project: `package.json` with extension manifest (contributes, activation events, configuration schema), `tsconfig.json`, `.vscodeignore`, `esbuild.mjs` bundler config, `.eslintrc.json`. **Test framework:** Mocha with `@vscode/test-electron` for integration tests (VS Code API mocking) — add as devDependencies: `mocha`, `@types/mocha`, `@vscode/test-electron`, `glob` | `ext/twig-vscode/package.json`, `tsconfig.json`, `.vscodeignore`, `esbuild.mjs`, `.eslintrc.json` | S | TO DO |
| T-1.2 | Implement `types.ts` — TypeScript interfaces for all twig CLI JSON output schemas: `WorkItem` (id, title, type, state, assignedTo, areaPath, iterationPath, isDirty, isSeed, parentId), `TreeData` (focus, parentChain, children, totalChildren, links), `PromptState`, `StateInfo`, `WorkItemSummary` (id, title, type, state — lightweight projection returned by `twig show --batch` and used by `showBatch()` in TwigCliClient) | `ext/twig-vscode/src/types.ts` | S | TO DO |
| T-1.3 | Implement `twig-client.ts` — CLI subprocess client with `execFile`, JSON parsing, timeout handling, error mapping, CLI discovery (`PATH` → `~/.twig/bin/twig` → setting), serial command queue, version check via `twig --version` | `ext/twig-vscode/src/twig-client.ts` | M | TO DO |
| T-1.4 | Implement `extension.ts` — entry point with `activate()` (workspace detection, service wiring) and `deactivate()` (disposal). Lazy activation on `workspaceContains:**/.twig/config` | `ext/twig-vscode/src/extension.ts` | S | TO DO |
| T-1.5 | Unit tests for TwigCliClient: CLI discovery, JSON parsing, timeout, error handling, serial queue, version check. **Test runner:** Mocha with `@vscode/test-electron` (see T-1.1). Mocking strategy: stub `child_process.execFile` with canned JSON responses | `ext/twig-vscode/src/test/suite/twig-client.test.ts`, `src/test/suite/index.ts` | M | TO DO |

**Acceptance Criteria**:
- [ ] Extension activates when workspace contains `.twig/config`
- [ ] Extension does NOT activate in non-twig workspaces
- [ ] `TwigCliClient` can discover and invoke `twig` binary
- [ ] CLI errors are mapped to VS Code notifications
- [ ] `npm run build` produces a bundled `.js` file via esbuild
- [ ] `npm run lint` passes with zero errors
- [ ] Unit tests pass with mocked CLI subprocess

---

### Issue 2: Status Bar & Prompt State Watcher

**Goal**: Show active work item context in the VS Code status bar, updated in real-time
via file watcher on `.twig/prompt.json`.

**Prerequisites**: Issue 1 (TwigCliClient, types)

**Satisfies**: FR-1, FR-2, NFR-2

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-2.1 | Implement `prompt-state-watcher.ts` — FileSystemWatcher on `.twig/prompt.json` with 100ms debounce, JSON parsing, typed event emitter, fallback for missing/empty file | `ext/twig-vscode/src/prompt-state-watcher.ts` | M | TO DO |
| T-2.2 | Implement `status-bar.ts` — StatusBarItem manager: formats `$(icon) #ID Title [State] •`, maps `stateCategory` to VS Code `ThemeColor` values for `backgroundColor` (since `StatusBarItem.color` is deprecated), click opens set-work-item command, tooltip with full details, empty state display | `ext/twig-vscode/src/status-bar.ts` | S | TO DO |
| T-2.3 | Wire PromptStateWatcher → StatusBarManager → TreeProvider in `extension.ts`, ensure proper disposal | `ext/twig-vscode/src/extension.ts` | S | TO DO |
| T-2.4 | Unit tests for PromptStateWatcher: file change detection, debounce, empty file handling, malformed JSON resilience | `ext/twig-vscode/src/test/suite/prompt-state-watcher.test.ts` | M | TO DO |

**Acceptance Criteria**:
- [ ] Status bar shows `$(circle-slash) Twig: No context` when no active item
- [ ] Status bar updates within 500ms of prompt.json change
- [ ] Status bar shows type badge, ID, truncated title, state
- [ ] Dirty indicator (•) shown when item has pending changes
- [ ] Clicking status bar opens "Twig: Set Work Item" command
- [ ] Extension handles missing/corrupt prompt.json gracefully

---

### Issue 3: Work Item Tree View

**Goal**: Provide a TreeView in the Explorer sidebar showing work item hierarchy with
expand/collapse navigation and click-to-set-context.

**Prerequisites**: Issue 1 (TwigCliClient), Issue 2 (PromptStateWatcher for refresh signals)

**Satisfies**: FR-3, FR-4, FR-5, FR-14

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-3.1 | Implement `tree-provider.ts` — TreeDataProvider with `getChildren()` (root: parent chain from `parentChain` array, focused item from `focus` field, children from `children` array with `totalChildren` count), `getTreeItem()` (icon, label, description, contextValue), auto-refresh on PromptStateWatcher events | `ext/twig-vscode/src/tree-provider.ts` | L | TO DO |
| T-3.2 | Register TreeView and context menu in `package.json` — view container in Explorer, view ID `twigWorkItems`, title "Twig Work Items", icon, Change State context menu wired to existing command | `ext/twig-vscode/package.json`, `ext/twig-vscode/src/commands.ts` | S | TO DO |
| T-3.3 | Unit tests for WorkItemTreeProvider: tree structure from JSON, node rendering, refresh behavior | `ext/twig-vscode/src/test/suite/tree-provider.test.ts` | M | TO DO |

**Acceptance Criteria**:
- [ ] TreeView appears in Explorer sidebar with "Twig Work Items" header
- [ ] Root shows active item's parent chain from top-level ancestor
- [ ] Active item is highlighted/bolded in the tree
- [ ] Expanding a node lazy-loads children via CLI
- [ ] Clicking a node sets it as active context
- [ ] Tree auto-refreshes when context changes (prompt.json update)
- [ ] Each node shows type badge, ID, title, and state
- [ ] Context menu on a node offers "Change State"

---

### Issue 4: Command Palette Integration

**Goal**: Register core twig workflow commands in VS Code's Command Palette with
appropriate input UI (input boxes, quick picks, progress notifications).

**Prerequisites**: Issue 1 (TwigCliClient), Issue 7 (`twig states` command for FR-7)

**Satisfies**: FR-6, FR-7, FR-8

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-4.1 | Implement `commands.ts` and register contributions in `package.json` — commands: setWorkItem (input box with validation), changeState (quick pick populated via `twig states --output json` with state category icons), sync (progress notification), refreshTree; all with "Twig:" prefix in palette. See **Change State error behavior** below. | `ext/twig-vscode/src/commands.ts`, `ext/twig-vscode/package.json` | M | TO DO |

> **T-4.1 — Change State error behavior:** If `twig states` exits non-zero (expected
> on CLI versions below `MINIMUM_CLI_VERSION`), show an error notification:
> _"Twig: Change State requires twig CLI ≥ {MINIMUM_CLI_VERSION}. Please upgrade:
> https://github.com/dangreen-msft/twig/releases"_ and do not open the quick pick.
> The `MINIMUM_CLI_VERSION` constant is defined in `twig-client.ts` (set to `0.0.0`
> during development, updated to the current twig release version at extension release time).

**Acceptance Criteria**:
- [ ] All commands appear in Command Palette with "Twig:" prefix
- [ ] "Set Work Item" accepts numeric ID or text search pattern
- [ ] "Change State" shows available states as quick pick with icons
- [ ] "Sync" shows progress notification during execution
- [ ] Errors show as VS Code error notifications with actionable buttons

---

### Issue 5: CodeLens Provider for AB# Annotations

**Goal**: Show inline CodeLens annotations for `AB#<id>` references in source files,
resolving work item title and state from the twig cache.

**Prerequisites**: Issue 1 (TwigCliClient), Issue 7 (`twig show --batch` for performance)

**Satisfies**: FR-10

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-5.1 | Implement `codelens-provider.ts` — Regex scan for `AB#(\d+)`, batched resolution via `twig show --batch <ids>`, Map-based TTL cache inlined in the provider (100 items, 5-min TTL), debounced re-scan (500ms), click action sets context; register in `extension.ts` with document selector for supported file types (md, txt, cs, ts, js, py, yaml, json) and `git-commit` language ID (VS Code's SCM commit message editor) | `ext/twig-vscode/src/codelens-provider.ts`, `ext/twig-vscode/src/extension.ts` | M | TO DO |
| T-5.2 | Unit tests for CodeLens: regex matching, cache hit/miss, TTL expiry, batch vs individual fallback | `ext/twig-vscode/src/test/suite/codelens-provider.test.ts` | M | TO DO |

**Acceptance Criteria**:
- [ ] `AB#1234` in any supported file shows CodeLens with work item info
- [ ] CodeLens displays: `📋 Task: Feature Title [Active]`
- [ ] Clicking CodeLens sets the referenced work item as active context
- [ ] Repeated views of the same file don't re-invoke CLI (TTL cached)
- [ ] Cache entries expire after 5 minutes
- [ ] Multiple AB# references resolved in a single CLI invocation (batch)

---

### Issue 6: CI/CD & Packaging

**Goal**: Add CI pipeline for the extension (lint, test, build) and release pipeline
for VSIX packaging and distribution.

**Prerequisites**: Issues 1-5 (all extension features complete)

**Satisfies**: G-6

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-6.1 | Add CI workflow job: `npm ci`, `npm run lint`, `npm test`, `npm run build` in `ext/twig-vscode/` | `.github/workflows/ci.yml` | S | TO DO |
| T-6.2 | Add release workflow job: build VSIX via `vsce package`, upload as release asset alongside CLI binaries | `.github/workflows/release.yml` | S | TO DO |
| T-6.3 | Write stub extension README and update root `README.md` with VS Code extension section | `ext/twig-vscode/README.md`, `README.md` | XS | TO DO |

**Acceptance Criteria**:
- [ ] CI runs extension tests on every PR
- [ ] Release workflow produces `twig-vscode-<version>.vsix`
- [ ] VSIX installs successfully in VS Code

---

### Issue 7: CLI Commands for Extension Support

**Goal**: Add two new CLI commands that fill data gaps required by the VS Code extension:
batched work item lookup for CodeLens and state enumeration for the state picker.

**Prerequisites**: None (independent of extension code; can be developed in parallel)

**Satisfies**: FR-7 (state picker data), FR-10 (CodeLens batching)

**Tasks**:

| Task ID | Description | Files | Effort | Status |
|---------|-------------|-------|--------|--------|
| T-7.1 | Add `--batch` flag to `ShowCommand`: accept comma-separated IDs via `twig show --batch 1234,5678,9012 --output json`, return JSON array of work item objects. Cache-only, no ADO fetch. Skip missing items silently. See **Implementation Notes** below for routing constraint. | `src/Twig/Commands/ShowCommand.cs`, `src/Twig/Program.cs`, `src/Twig/Formatters/JsonOutputFormatter.cs` | M | TO DO |
| T-7.2 | Add `StatesCommand`: `twig states --output json` returns available states (name, category, color) for the active work item's type from `IProcessTypeStore`. Reads from local cache, no network call. | `src/Twig/Commands/StatesCommand.cs`, `src/Twig/Program.cs` | M | TO DO |
| T-7.3 | Tests for `ShowCommand --batch`: empty list, single ID, multiple IDs, missing IDs, JSON format | `tests/Twig.Cli.Tests/Commands/ShowBatchTests.cs` | M | TO DO |
| T-7.4 | Tests for `StatesCommand`: no active item, active item with known type, JSON output validation | `tests/Twig.Cli.Tests/Commands/StatesCommandTests.cs` | M | TO DO |

#### Implementation Notes: ConsoleAppFramework Routing Constraint

> **Critical for T-7.1:** The existing `Show` method uses `[Argument] int id` as a positional
> parameter (see `Program.cs` line 325). ConsoleAppFramework does not support optional positional
> arguments alongside named options in the same method. T-7.1 **must** register `--batch` as a
> separate overload (e.g., `ShowBatch([Option] string batch, ...)`) or as a distinct subcommand,
> to avoid ambiguous routing between `twig show 1234` and `twig show --batch 1234,5678`.
> This is a hard framework constraint — attempting to add `--batch` to the existing `Show` method
> will produce a compile-time error or runtime routing ambiguity.

**Acceptance Criteria**:
- [ ] `twig show --batch 1,2,3 --output json` returns JSON array of 3 work items
- [ ] Missing IDs are silently skipped in batch output
- [ ] `twig states --output json` returns states with name, category, and color
- [ ] `twig states` errors gracefully when no active item is set
- [ ] Both commands registered in `KnownCommands` set
- [ ] Both commands work with `--output json` and `--output human` formats

---

## PR Groups

PR groups cluster tasks for reviewable pull requests. They are sized for review efficiency
(≤2000 LoC, ≤50 files) and are NOT a 1:1 mapping to the ADO Issue hierarchy.

### Execution Dependency Matrix

```
PG-1 (CLI Commands)  ──┬──→ PG-3 (Tree View & Commands)  ──→ PG-4 (CodeLens & CI)
PG-2 (Scaffold)       ─┘                                  ──→ PG-4
```

PG-1 and PG-2 can be developed and reviewed in parallel; both must merge before PG-3
can start. PG-4 requires all prior groups.

---

### PG-1: CLI Extension Support Commands

**Type**: Deep (new commands with JSON output, domain service wiring)
**Issues**: Issue 7 (all tasks)
**Estimated LoC**: ~600
**Files**: ~8
**Predecessor**: None (can be developed immediately)
**Successor**: PG-3, PG-4

**Description**: Adds `twig show --batch` and `twig states` CLI commands. These are
pure C# changes in the existing CLI project, independent of the TypeScript extension.
Can be reviewed and merged early to unblock PG-3 and PG-4. Listed first because it is
on the critical path for the state picker (PG-3) and CodeLens batching (PG-4).

**Key review focus**: AOT compatibility (manual `Utf8JsonWriter` usage), process-agnostic
design (no hardcoded state names), `KnownCommands` set updates, test coverage.

---

### PG-2: Extension Scaffold, Core Client & Status Bar

**Type**: Deep (project scaffold + reactive status infrastructure)
**Issues**: Issue 1 (all tasks) + Issue 2 (all tasks)
**Estimated LoC**: ~1300
**Files**: ~18
**Predecessor**: None (can be developed in parallel with PG-1)
**Successor**: PG-3, PG-4

**Description**: Sets up the entire VS Code extension project structure, the core `TwigCliClient`,
and the passive status bar + file watcher. Issues 1 and 2 are batched here because Issue 2 is
small (~500 LoC, 6 files) and has no work that benefits from a separate review cycle — it simply
wires a file watcher to a status bar item. Wiring of both StatusBarManager and TreeProvider into
`extension.ts` is a single task (T-2.3).

**Key review focus**: Extension manifest correctness (activation events, configuration
schema), CLI client error handling, serial queue implementation, file watcher reliability,
debounce logic, status bar formatting, test coverage.

---

### PG-3: Tree View & Commands

**Type**: Wide (many files touched, but largely declarative package.json + command wiring)
**Issues**: Issue 3 (all tasks) + Issue 4 (all tasks)
**Estimated LoC**: ~1200
**Files**: ~8
**Predecessor**: PG-1 (needs `twig states` for state picker), PG-2 (needs TwigCliClient)
**Successor**: PG-4

**Description**: Combined tree view + command palette PR. These are tightly coupled —
tree context menus invoke the same commands registered in the palette, and both consume
TwigCliClient. Reviewing together avoids redundant context-building. Add Note and Open in
Browser commands are deferred (NG-7); commands scope is set/state/sync/refresh.

**Key review focus**: TreeDataProvider lazy loading (correct use of `focus`/`parentChain`/
`children`/`totalChildren` fields from tree JSON), command input validation, state quick
pick UX with category icons, fallback for old CLI versions, `package.json` contribution
points.

---

### PG-4: CodeLens & CI

**Type**: Deep (CodeLens resolution logic) + Wide (CI pipeline changes)
**Issues**: Issue 5 (all tasks) + Issue 6 (all tasks)
**Estimated LoC**: ~900
**Files**: ~12
**Predecessor**: PG-1 (needs `twig show --batch` for performance), PG-2, PG-3

**Description**: CodeLens provider for AB# annotations with TTL caching and batched
resolution, plus CI/CD pipeline and VSIX packaging. Grouped together because CodeLens
is the last feature, and CI/packaging validates the complete extension.

**Key review focus**: TTL cache correctness, batch vs individual fallback logic, regex
pattern edge cases, CI workflow configuration, VSIX packaging.

---

## References

- [VS Code Extension API](https://code.visualstudio.com/api)
- [VS Code TreeView API](https://code.visualstudio.com/api/extension-guides/tree-view)
- [VS Code CodeLens API](https://code.visualstudio.com/api/references/vscode-api#CodeLensProvider)
- [VS Code Extension Testing](https://code.visualstudio.com/api/working-with-extensions/testing-extension)
- [esbuild for VS Code Extensions](https://code.visualstudio.com/api/working-with-extensions/bundling-extension)
- [VSCE Packaging Tool](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)
- twig prompt.json schema: `src/Twig.Infrastructure/Config/PromptStateWriter.cs`
- twig JSON output contract: `src/Twig/Formatters/JsonOutputFormatter.cs`
- twig tree JSON: `FormatTree()` at line 99 — `{ focus, parentChain, children, totalChildren, links }`
- twig workspace JSON: `FormatWorkspace()` at line 146 — `{ context, sprintItems, seeds, staleSeeds, dirtyCount }`
- twig state transition: `src/Twig.Domain/Services/StateTransitionService.cs`
- twig process types: `src/Twig.Domain/Aggregates/ProcessTypeRecord.cs` (states per type)
- twig MCP server prior art: `src/Twig.Mcp/Program.cs`
- twig CLI command registry: `src/Twig/Program.cs` lines 845–944 (`KnownCommands` set)

---

## Appendix: Resolved Design Decisions

The following questions from the initial draft have been resolved and promoted to design
decisions. Preserved here for decision history and traceability:

- **Multi-root workspace support**: Initial version uses first workspace folder with `.twig/config`. Multi-root support deferred. (See DD-6)
- **CodeLens remote fetch**: Yes, `twig show <id>` auto-fetches on cache miss. Cache the result in the TTL cache. (See DD-4)
- **CLI bundling vs separate install**: Require separate installation. Bundling would need platform-specific VSIX variants and increase package size from <500KB to ~20MB. (See Dependencies § Internal)
- **`twig states` scope**: Returns states for the active item's type only — sufficient for the extension's state picker and simpler than returning all types. (See DD-8)
- **Minimum CLI version (OQ-1)**: Resolved per stakeholder input — the minimum version is
  whatever the most recent twig CLI release version is when the extension becomes ready to
  ship. Strategy: the `MINIMUM_CLI_VERSION` constant in `twig-client.ts` is set to `0.0.0`
  during development (accept any version) and updated to the actual release version at
  extension release time. This release will necessarily include the `twig show --batch` and
  `twig states` commands (Issue 7). The version-check guard is a P0 activation behavior —
  it blocks all extension functionality when the CLI is too old. (See TwigCliClient § Version check)

