---
goal: TWIG (Terminal Work Integration Gadget) — A git-like CLI for ADO work context management
version: 0.1
date_created: 2026-03-09
last_updated: 2026-03-09
owner: Daniel Green
tags: feature, cli, ado, developer-tools, work-management
---

# Introduction

Requirements Document for the following initiative: TWIG (Terminal Work Integration Gadget) — a high-performance, opinionated CLI tool for Azure DevOps work item context management. TWIG applies a git-like interaction model to ADO work tracking: fast local reads, terse commands, context-aware operations, and seamless integration with AI agent workflows. It is designed for developers who live in the terminal and want to manage their ADO work items without leaving it.

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

**Cross-reference conventions**: Functional requirements use the `FR-` prefix, non-functional requirements use `NFR-`, failure modes use `FM-`, and acceptance criteria use `AC-`. These prefixes enable traceability across sections and into the PRD.

## 1. Terminology

| Term | Definition |
|------|------------|
| TWIG | Terminal Work Integration Gadget. The CLI tool being designed. |
| Work Context | The currently active work item and its surrounding tree (parent chain to root, children to one level of depth). Includes sprint/iteration awareness. |
| Set (Context) | Setting a work item as the active context via `twig set <id|pattern>`. Accepts work item IDs or partial text patterns (case-insensitive substring match). Replaces the git `checkout` metaphor with a more accurate "set context" model. |
| Seed | A minimally-populated work item created as a placeholder for later fleshing out. Inherits defaults (area path, iteration, parent) from the current context. "Seed" aligns with the TWIG botanical naming convention. |
| State Shorthand | Single-character aliases for ADO work item states: `p` (Proposed), `c` (Committed), `s` (Started), `d` (Done/Completed), `x` (Cut). |
| Dirty Item | A work item with local changes that have not yet been saved to ADO. |
| Work Tree | The hierarchical structure of work items (Epic → Scenario/Feature → Deliverable → Task Group → Task). |
| Workspace | The aggregate view of a user's active work: current context + sprint items + seeds. The default scope for listing operations. |
| Update | A single-field write operation that auto-syncs with ADO (pull, apply, push, resolve conflicts). Immediate and atomic. |
| Edit | An open-ended editing session that stages multiple changes locally. Changes are persisted to ADO when the user explicitly runs `save`. |
| Save | Persists all staged edits and any pending notes to ADO. Resolves conflicts. Closes the edit session. |
| `.twig/` | Directory in the repository root containing TWIG's local state, configuration, and cached work item data. |
| TUI | Terminal User Interface — interactive panel-based views for tree navigation and work item editing. Post-MVP. |
| AOT | Ahead-of-Time compilation. .NET Native AOT produces a single native binary with no runtime dependency. |

## 2. Scope

### In Scope

- CLI tool for ADO work item context management (read and write operations)
- Work context model: set context, status, navigation, state transitions
- Sprint/iteration awareness — TWIG understands the current sprint and can scope operations accordingly
- Work item creation via seeds (minimal placeholders) with context-inherited defaults
- Local-first note-taking with bulk push to ADO
- Local state management in `.twig/` directory
- Authentication via Azure CLI token (default) with PAT fallback
- Per-repository configuration
- Non-interactive CLI commands covering all use cases
- Structured output modes for AI/automation consumption (JSON, minimal)
- Integration surface design for prompts, AI skills, and future MCP exposure

### Out of Scope (deferred)

- TUI (Terminal User Interface) — design considerations captured here for data model awareness, but implementation is post-MVP
- MCP server exposure — future phase, after CLI is stable
- Cross-repository work item management — V1 is single-repo context
- ADO Boards views — TWIG is tree-and-item focused, not board-view focused
- Exposed WIQL query language — TWIG MAY use WIQL internally for efficient data retrieval, but the query language is never exposed to the user
- Attachments and linked artifacts (PRs, branches, builds) — future enrichment
- Multi-user collaboration features — V1 is single-user context

## 3. Functional Requirements

- **FR-001**: Work Context Management
    - **Description**: TWIG MUST support setting a single work item as the active context via the `set` command. The active context includes awareness of the parent chain up to the root work item, children to at least one level of depth (depth MAY vary by work item type), and the current sprint/iteration.
    - **Acceptance Criteria**:
        - `twig set <id>` sets the active work item and persists it in `.twig/`
        - `twig set <pattern>` accepts partial text patterns (case-insensitive substring) to resolve a work item (prompts for disambiguation if multiple matches)
        - `twig status` displays the current work item with key fields (title, state, assigned to, area, iteration, sprint)
        - Parent chain is resolved and cached on set
        - Children at one level of depth are resolved and cached on set
        - Current sprint/iteration is resolved and available to context-aware operations
    - **Priority**: High
    - **Dependencies**: FR-005 (Local State), FR-007 (Authentication)

- **FR-002**: State Transitions
    - **Description**: TWIG MUST support changing work item state via single-character shorthand commands. Forward-sequential transitions (`p → c → s → d`) MUST proceed without confirmation. Out-of-sequence or backward transitions MUST prompt for user confirmation. The `x` (Cut) transition MUST always prompt for confirmation.
    - **Acceptance Criteria**:
        - `twig state <shorthand>` changes the state of the active work item
        - Standard types: `p` (Proposed), `c` (Committed), `s` (Started), `d` (Done/Completed), `x` (Cut)
        - Bug types: `a` (Active), `r` (Resolved), `d` or `c` (Closed)
        - Forward transitions execute immediately
        - Backward transitions (e.g., `s → p`) display confirmation prompt
        - `x` (Cut) always displays confirmation prompt and MUST require a reason (maps to the ADO Reason field)
        - State change is pushed to ADO immediately (write-through)
        - TWIG MUST detect conflicts when the cached work item revision differs from the server revision before writing, and handle them intelligently (e.g., re-fetch, merge non-conflicting fields, prompt user on true conflicts)
    - **Priority**: High
    - **Dependencies**: FR-001 (Work Context)

- **FR-003**: Seed Creation
    - **Description**: TWIG MUST support creating minimally-populated work items ("seeds") that inherit context from the current position in the work tree. Seeds are intended as placeholders for later fleshing out and review.
    - **Acceptance Criteria**:
        - `twig seed "title"` creates a new work item with the given title
        - `twig seed --type <type> "title"` allows explicitly specifying the work item type; if omitted, the child type is inferred from the current context and hierarchy rules
        - The default child type for each parent type is configurable via settings (e.g., `twig config seed.default_child_type.Deliverable Task`)
        - Seed inherits area path from current context
        - Seed inherits iteration from current context
        - If the seed's inferred type is a valid child type of the current context (e.g., Task under a Deliverable), the seed MUST be automatically parented to the current context item
        - Work item type is inferred from the parent type and tree hierarchy rules
        - Seed is created in `Proposed` state
        - Seed creation is pushed to ADO immediately and the new ID is returned
        - Seeds MUST always appear in workspace listings (see FR-014)
        - Stale seeds (not completed or abandoned within a configurable time period) MUST generate warnings when TWIG displays workspace or status output
    - **Priority**: High
    - **Dependencies**: FR-001, FR-006 (ADO Process Configuration), FR-011 (Configuration for stale seed threshold and default child types)

- **FR-004**: Notes and Comments
    - **Description**: TWIG MUST support local-first note-taking on work items. Notes are stored locally in `.twig/` and auto-pushed to ADO when any `update` operation occurs, or persisted when `save` is run (even without an active edit session).
    - **Acceptance Criteria**:
        - `twig note "text"` appends a timestamped note to the active work item locally
        - `twig note` (no argument) opens `$EDITOR` for multi-line note composition
        - Pending notes are automatically pushed to ADO as discussion comments when any `update` command is executed on the same work item
        - Pending notes are pushed to ADO when `save` is run, even if no edit session is active
        - Notes are timestamped with local time on creation
        - Items with unpushed notes are indicated as dirty
    - **Priority**: High
    - **Dependencies**: FR-001, FR-005

- **FR-005**: Local State Management
    - **Description**: TWIG MUST maintain local state in a `.twig/` directory at the repository root. This includes the active context, cached work item data, unpushed notes, and configuration.
    - **Acceptance Criteria**:
        - `.twig/` directory is created on `twig init`
        - Active context (current work item ID) persists across shell sessions
        - Cached work item fields are available for fast reads
        - Unpushed local changes (notes, field edits) are tracked per work item
        - Dirty state is indicated with a `•` marker in status and tree views
    - **Priority**: High
    - **Dependencies**: None (foundational)

- **FR-006**: ADO Process Awareness
    - **Description**: TWIG MUST be aware of the ADO project's process configuration, including work item types, state definitions, and parent/child hierarchy rules. This configuration SHOULD be cached locally after initial fetch.
    - **Acceptance Criteria**:
        - `twig init` fetches and caches the project's work item types and their states
        - State shorthand mappings are derived from the actual process configuration
        - Parent/child hierarchy rules are enforced when creating seeds
        - Process configuration can be refreshed on demand
    - **Priority**: High
    - **Dependencies**: FR-007
    - **Notes**: The OS project uses a custom process with the following key types and states:
        - **Standard types** (Task, Task Group, Deliverable, Feature, Epic, Scenario): Proposed → Committed → Started → Completed / Cut
        - **Bug**: Active → Resolved → Closed
        - **Hierarchy**: Epic → Scenario/Feature → Deliverable/Bug → Task (Task Group is situational)

- **FR-007**: Authentication
    - **Description**: TWIG MUST authenticate to ADO using Azure CLI tokens by default, with PAT-based authentication as a fallback. Authentication configuration is stored in `.twig/`.
    - **Acceptance Criteria**:
        - Default: uses `az account get-access-token` for the ADO resource
        - Fallback: PAT stored in `.twig/config` or environment variable
        - Auth method is configurable per repository
        - Clear error message when no valid authentication is available
        - Token refresh is handled transparently for az cli mode
    - **Priority**: High
    - **Dependencies**: None (foundational)

- **FR-008**: Work Tree Navigation
    - **Description**: TWIG MUST support navigating the work item hierarchy via CLI commands. Users can move up to parents, down to children, and view the tree structure. All ID-based navigation commands MUST support partial text patterns (case-insensitive substring) in addition to numeric IDs.
    - **Acceptance Criteria**:
        - `twig tree` displays the work tree from the current context (parents up, children down)
        - `twig up` moves context to the parent work item
        - `twig down <id|pattern>` moves context to a child work item, supporting partial text matching
        - If a pattern matches multiple items, TWIG MUST prompt the user to disambiguate
        - Tree display shows work item type, title, state shorthand, and dirty indicator
        - Tree depth and scope are configurable per repository
    - **Priority**: High
    - **Dependencies**: FR-001, FR-005, FR-016

- **FR-009**: Work Item Field Operations (Update and Edit)
    - **Description**: TWIG MUST support two modes of field modification:
        - **Update** (`twig update <field> <value>`): A single-field atomic write. Automatically pulls the latest revision from ADO, applies the change, pushes, and resolves conflicts. Also auto-pushes any pending notes. This is the fast path for quick changes.
        - **Edit** (`twig edit`): Opens an editing session that stages multiple changes locally. Changes are persisted to ADO only when `twig save` is executed. `save` also pushes any pending notes.
    - **Acceptance Criteria**:
        - `twig update title "new title"` immediately syncs the title change to ADO (pull-apply-push)
        - `twig update tags "tag1, tag2"` immediately syncs tags to ADO
        - `twig update assign "alias"` immediately syncs assignment to ADO
        - Any `update` command also auto-pushes pending notes on the same work item
        - `twig edit` opens the full work item in `$EDITOR` as a structured temp file, staging changes locally
        - `twig edit description` opens only the description in `$EDITOR`, staging locally
        - `twig save` persists all staged edits and pending notes to ADO, resolving conflicts
        - `twig save` MAY be run even without an active edit session to push pending notes
        - Staged (unsaved) edits are indicated as dirty in status and tree views
    - **Priority**: High
    - **Dependencies**: FR-001, FR-005

- **FR-010**: Sync and Refresh
    - **Description**: TWIG MUST support refreshing the local cache from ADO. Explicit push/pull/sync commands are replaced by implicit sync behavior built into `update` and `save` operations. A manual refresh command is available for cache staleness.
    - **Acceptance Criteria**:
        - `twig refresh` refreshes the local cache for the current workspace from ADO
        - `update` commands implicitly pull-then-push for the target field (FR-009)
        - `save` commands implicitly push all staged edits and pending notes (FR-009)
        - Sync results are reported (success/failure per item)
        - Conflicts are detected and reported inline during `update` and `save`
    - **Priority**: High
    - **Dependencies**: FR-005, FR-007

- **FR-011**: Repository Configuration
    - **Description**: TWIG MUST support per-repository configuration stored in `.twig/config`. Configuration includes ADO organization, project, area path defaults, iteration defaults, tree depth, and display preferences.
    - **Acceptance Criteria**:
        - `twig init` prompts for or accepts ADO organization and project
        - Configuration is stored in `.twig/config` in a human-readable format
        - Defaults for area path, iteration, and work item type hierarchy are configurable
        - Configuration can be edited directly or via `twig config <key> <value>`
    - **Priority**: High
    - **Dependencies**: FR-005

- **FR-012**: Structured Output for AI Integration
    - **Description**: TWIG MUST support structured output modes suitable for consumption by AI agents, prompts, and automation scripts.
    - **Acceptance Criteria**:
        - `--output json` flag produces machine-readable JSON for any command
        - `--output minimal` flag produces terse single-line output suitable for prompt injection
        - Default output is human-readable formatted text
        - JSON output schema is stable and documented
    - **Priority**: Medium
    - **Dependencies**: None

- **FR-013**: Initialization
    - **Description**: TWIG MUST support initializing a repository for TWIG usage, similar to `git init`.
    - **Acceptance Criteria**:
        - `twig init` creates the `.twig/` directory structure
        - Prompts for ADO organization and project (or accepts via flags)
        - Fetches and caches process configuration (work item types, states, hierarchy rules)
        - Sets up authentication configuration
        - Validates connectivity to ADO
    - **Priority**: High
    - **Dependencies**: FR-005, FR-006, FR-007

- **FR-014**: Workspace View
    - **Description**: TWIG MUST provide a workspace view that aggregates the user's active work: current context, sprint items, and seeds. This is the default scope for listing operations. `workspace` supports subcommands; `twig show` is a top-level shorthand for `twig workspace show`.
    - **Acceptance Criteria**:
        - `twig workspace show` (or `twig show`, or `twig ws show`) displays the combined workspace: current context item, items in the current sprint, and all seeds
        - `twig workspace` with no subcommand defaults to `show`
        - Seeds MUST always be included in workspace listings regardless of sprint assignment
        - Stale seeds (exceeding the configurable threshold, default TBD) are displayed with a warning indicator
        - The stale seed threshold is configurable via `twig config seed.stale_days <n>`
        - Workspace view shows work item type, title, state shorthand, dirty indicator, and stale warning where applicable
        - Additional `workspace` subcommands MAY be added in future iterations
    - **Priority**: High
    - **Dependencies**: FR-001, FR-003, FR-005, FR-011

- **FR-015**: Helpful Command Hints
    - **Description**: TWIG SHOULD display contextual "next command" hints after operations, similar to git's helpful output (e.g., "use `twig save` to persist your edits" or "use `twig state s` to mark as started"). Hints SHOULD be suppressible via configuration.
    - **Acceptance Criteria**:
        - After state changes, suggest logical next actions (e.g., after `twig state d`, suggest closing parent if all children are done)
        - After `twig set`, suggest common operations (`state`, `note`, `tree`)
        - After `twig seed`, suggest setting context to the new item or creating more seeds
        - After `twig edit`, remind user to `save` when done
        - Stale seed warnings include a hint to complete or cut the seed
        - Hints can be disabled via `twig config hints false`
        - Hints are suppressed when `--output json` or `--output minimal` is used
    - **Priority**: Medium
    - **Dependencies**: FR-012 (output modes)

- **FR-016**: Partial Match Resolution
    - **Description**: All TWIG commands that accept a work item identifier MUST support partial text patterns (case-insensitive substring match) in addition to numeric IDs. This enables fast navigation without requiring exact ID recall.
    - **Acceptance Criteria**:
        - Numeric IDs are matched exactly
        - Text patterns are matched as case-insensitive substrings against work item titles within the current workspace/tree scope
        - If a pattern matches exactly one item, it is selected automatically
        - If a pattern matches multiple items, TWIG MUST prompt the user to disambiguate (interactive list)
        - In non-interactive mode (`--output json`), multiple matches return all candidates without prompting
    - **Priority**: High
    - **Dependencies**: FR-005 (Local State for cached titles)

## 4. Non-Functional Requirements

- **NFR-001**: CLI Responsiveness
    - **Metric:** Read operations from local cache MUST complete in < 200ms. Write operations to ADO are expected to take 200-800ms and this is acceptable.
    - **Rationale:** "Comparable to git" performance for local reads. Network-bound writes are accepted as inherently slower.
    - **Testing Approach:** Benchmark suite measuring p95 latency for `status`, `tree`, `state`, `note` commands against local cache.

- **NFR-002**: Single Binary Distribution
    - **Metric:** TWIG MUST compile to a single native binary with no runtime dependencies via .NET Native AOT.
    - **Rationale:** Zero-friction installation, comparable to distributing a Go or Rust binary. No requirement for .NET runtime on target machine.
    - **Testing Approach:** Verify AOT compilation produces functional single-file binary on Windows, Linux, macOS.

- **NFR-003**: Offline-Capable Reads
    - **Metric:** All read operations (status, tree, notes) MUST work without network connectivity using the local cache.
    - **Rationale:** Developers may work offline (travel, network issues) and should still be able to view and navigate their work context.
    - **Testing Approach:** Run read commands with network adapter disabled after initial sync.

- **NFR-004**: Startup Time
    - **Metric:** Cold start time MUST be < 100ms. Warm start time SHOULD be < 50ms.
    - **Rationale:** AOT compilation enables fast startup. CLI tools that take > 500ms to start feel sluggish.
    - **Testing Approach:** Measure process start to first output for `twig status`.

- **NFR-005**: Cross-Platform
    - **Metric:** TWIG MUST run on Windows (primary), Linux, and macOS.
    - **Rationale:** Developer workstations vary. .NET AOT supports all three platforms.
    - **Testing Approach:** CI builds and test suites on all three platforms.

## 5. Failure Modes and Recovery

| ID | Failure | Detection | Recovery |
|----|---------|-----------|----------|
| FM-001 | ADO API unreachable (network down, service outage) | HTTP timeout or connection refused | Graceful degradation to read-only local cache. All pending writes (staged edits, notes) are preserved in `.twig/` and clearly surfaced. `twig status` reports items with unsaved remote changes. User retries explicitly via `twig save` or `twig update` when connectivity returns. No local work is lost. |
| FM-002 | Azure CLI token expired | 401 response from ADO API | Prompt user to run `az login`. Provide clear error message. |
| FM-003 | PAT expired or revoked | 401 response from ADO API | Prompt user to update PAT in `.twig/config` or environment variable. |
| FM-004 | Work item not found | 404 response from ADO API | Clear error message with the ID. Suggest verifying the ID or project. |
| FM-005 | State transition not allowed by ADO process rules | 400 response from ADO API | Display the allowed transitions for the current state. Do not update local cache. |
| FM-006 | Conflict on write (cached revision differs from server) | Revision mismatch on ADO API response (412 or field-level conflict) | Auto-refresh the work item from server. If only non-conflicting fields changed remotely, merge and retry. If the field being written was also changed remotely, report the conflict with both values and prompt the user. MUST NOT silently overwrite. |
| FM-007 | Stale cache on state change | Cached state differs from server state when attempting transition | Re-fetch current state from ADO. If the transition is still valid from the actual server state, proceed. If not, report the actual state and valid transitions. |
| FM-008 | `.twig/` directory missing or corrupted | File I/O error or schema validation failure | Prompt user to run `twig init` to reinitialize. Preserve any recoverable local data. |
| FM-009 | Process configuration changed on server | Cached work item type or state no longer valid | Detect on next API call. Auto-refresh process config. Warn user if cached state mappings changed. |

## 6. Acceptance Criteria

| ID | Criterion | Verification | Traces To |
|----|-----------|--------------|-----------|
| AC-001 | `twig init` creates `.twig/` directory and fetches process config | Automated test | FR-005, FR-006, FR-013 |
| AC-002 | `twig set <id>` sets active context with parent chain, children, and sprint | Automated test | FR-001 |
| AC-003 | `twig status` displays current item fields in < 200ms from cache | Automated test + benchmark | FR-001, NFR-001 |
| AC-004 | `twig state s` transitions from Committed to Started without confirmation | Automated test | FR-002 |
| AC-005 | `twig state p` from Started prompts for confirmation | Automated test | FR-002 |
| AC-006 | `twig state x` always prompts for confirmation | Automated test | FR-002 |
| AC-007 | `twig state x` requires a reason before proceeding | Automated test | FR-002 |
| AC-008 | Bug work items accept both `d` and `c` for Closed state | Automated test | FR-002, FR-006 |
| AC-009 | `twig seed "title"` creates work item inheriting parent context | Automated test | FR-003 |
| AC-010 | `twig seed` under a Task Group auto-parents the new Task | Automated test | FR-003, FR-006 |
| AC-011 | Stale seeds display warning in workspace and status output | Automated test | FR-003, FR-014 |
| AC-012 | `twig note "text"` stores timestamped note locally without network call | Automated test | FR-004, NFR-003 |
| AC-013 | `twig update title "new"` auto-syncs to ADO and pushes pending notes | Integration test | FR-009, FR-004 |
| AC-014 | `twig edit` opens editor, `twig save` persists staged changes and pending notes to ADO | Integration test | FR-009 |
| AC-015 | `twig tree` displays hierarchy with state shorthand and dirty markers | Automated test | FR-008 |
| AC-016 | `twig workspace` shows context + sprint items + all seeds | Automated test | FR-014 |
| AC-017 | Contextual hints displayed after operations, suppressed with `--output json` | Automated test | FR-015, FR-012 |
| AC-018 | `--output json` produces valid, parseable JSON for all commands | Automated test | FR-012 |
| AC-019 | Binary runs on Windows, Linux, macOS with no runtime dependencies | CI pipeline | NFR-002, NFR-005 |
| AC-020 | All read commands work offline after initial sync | Automated test | NFR-003 |

---

## Open Design Investigations

The following areas require further analysis before architectural decisions can be finalized. These will be addressed in the PRD phase.

### DI-001: Cache / Local Index Architecture

**Question**: How should TWIG maintain its local index for fast reads?

**Candidates**:
- **SQLite local DB** — Structured queries, battle-tested, supports offline. Git-for-Windows ships SQLite.
- **Flat file index** (like git) — Custom binary/JSON index files in `.twig/`. Maximum control, harder to query.
- **In-memory + lazy fetch** — No persistent cache. Fast startup but every command hits ADO. Won't meet NFR-001.

**Evaluation Criteria**: Read latency (NFR-001), startup time (NFR-004), offline support (NFR-003), complexity, data model flexibility for future TUI scenarios, sync/conflict semantics.

**Status**: Requires scenario-driven evaluation with full command surface defined.

### DI-002: CLI Command Surface

**Question**: What is the complete `twig <verb>` command tree?

**Current Draft** (from discussion):

```
twig init                          # Initialize .twig/ for a repository
twig set <id|pattern>              # Set active work context (supports fuzzy match)
twig status                        # Show current work item details
twig show                          # Shorthand for `twig workspace show`
twig state <p|c|s|d|x>            # Transition work item state (x requires reason)
twig tree                          # Display work tree hierarchy
twig up                            # Move context to parent
twig down <id|pattern>             # Move context to child (supports fuzzy match)
twig seed [--type <type>] "title"  # Create a seed work item (type inferred, auto-parents)
twig note ["text"]                 # Add note (inline or $EDITOR) — auto-pushed on update/save
twig update <field> <value>        # Single-field atomic write (auto pull/push/resolve)
twig edit [field]                  # Open editing session (staged locally)
twig save                          # Persist staged edits + pending notes to ADO
twig refresh                       # Refresh local cache from ADO
twig workspace [show]              # Show workspace: context + sprint + seeds (aliases: ws, show)
twig config <key> <value>          # Set configuration
```

**Status**: Draft. Needs validation against real workflows and edge cases.

### DI-003: Data Model

**Question**: What is the internal data representation for work items, tree relationships, and local state?

**Constraints**:
- Must support the ADO OS process types (32 types, 7 primary: Task, Task Group, Deliverable, Feature, Epic, Scenario, Bug)
- Must model parent/child hierarchy with configurable depth
- Must track dirty state per field per item
- Must support efficient tree traversal for TUI navigation (post-MVP)
- Must handle concurrent local edits and remote changes

**Status**: Requires cache architecture decision (DI-001) first.

### DI-004: TUI Design

**Question**: How should interactive terminal UI be designed for tree navigation and work item editing?

**Decisions Made**:
- Framework: Terminal.Gui (post-MVP)
- Two primary views: Tree Navigator and Work Item Form Editor
- Vim-style keybindings (j/k, enter, space)
- Text editing: Inline default + `$EDITOR` on demand (both supported)
- Dirty indicators: `•` marker on items with unpushed changes

**Status**: Post-MVP. Captured here for data model awareness.

---

## Resolved Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| RD-001 | C# with .NET Native AOT | Single binary distribution (NFR-002), strong ADO SDK support, good AI library ecosystem, team familiarity. Preferred over Rust (harder AI integration), Go (weaker ADO SDK), TypeScript (performance ceiling). |
| RD-002 | Azure CLI auth default, PAT fallback | Zero-config for corp users with existing `az login`. PAT covers CI/automation scenarios. |
| RD-003 | `.twig/` in repository root | Matches `.git/` convention. Per-repo isolation. Easy to `.gitignore`. |
| RD-004 | Per-repository configuration | Different repos may target different ADO orgs, projects, area paths. Config in `.twig/config`. |
| RD-005 | Read + write for V1 | Full work context lifecycle from day one. Read-only would limit the tool's value proposition. |
| RD-006 | State shorthand: `p/c/s/d/x` | Single-character input for maximum speed. `d` for Done avoids collision between Committed (`c`) and Completed. Bug alias: `d` and `c` both map to Closed. |
| RD-007 | Out-of-sequence state transitions require confirmation | Safety net against accidental state regression. Forward-sequential is the happy path and should be frictionless. `x` (Cut) always confirms as it's destructive. |
| RD-008 | Cut requires a reason | Maps to ADO's Reason field. Prevents accidental cuts without documentation. |
| RD-009 | Two write modes: `update` (atomic) and `edit`/`save` (session) | `update` is the fast path for single-field changes — auto pull/push/resolve with no staging. `edit`/`save` is for open-ended multi-field sessions. Eliminates need for explicit push/pull/sync commands. |
| RD-010 | Text editing: inline + $EDITOR | Both modes supported. Inline for quick single-line edits. `$EDITOR` for multi-line content (description, long notes). Matches git's `commit -m` vs `commit` pattern. |
| RD-011 | Local-first notes, auto-push on update/save | Instant note capture without network latency. Notes are auto-pushed when any `update` is performed or when `save` is run. No separate push step needed. |
| RD-012 | Seeds inherit context from current position | Reduces friction for quick work item creation. Area path, iteration, and parent are derived from the current work context. |
| RD-013 | Seeds auto-parent to current context | If the inferred child type is valid for the current context, the seed is automatically parented. Reduces manual linking. |
| RD-014 | Seed type is optional and configurable | `--type` flag overrides inferred child type. Default child type per parent type is configurable in settings. |
| RD-015 | Stale seed warnings | Seeds not completed or abandoned within a configurable threshold generate warnings in workspace/status output. Prevents forgotten placeholder items. |
| RD-016 | Tasks parent to Deliverables by default | The default hierarchy is Epic → Scenario/Feature → Deliverable → Task. Task Groups are situational, not the default parent for Tasks. |
| RD-017 | Workspace = context + sprint + seeds | The default listing scope aggregates all active work. Seeds always appear regardless of sprint assignment. |
| RD-018 | `twig show` as top-level shorthand | `twig show` is an alias for `twig workspace show`. Common operation deserves a short path. |
| RD-019 | Partial match for all ID navigation | All commands accepting work item IDs also accept partial text patterns (case-insensitive substring). Disambiguates interactively on multiple matches. |
| RD-020 | Helpful next-command hints | Git-style contextual suggestions after operations. Suppressible via config or structured output modes. Reduces learning curve. |
| RD-021 | TUI is post-MVP | Significant implementation effort. CLI covers all use cases non-interactively. TUI adds ergonomics but isn't required for core value. Data model must account for TUI needs. |

## ADO Process Reference (OS Project)

### Work Item Types (32 total)

Bug, Customer Promise, Deliverable, Dependency, Epic, Experiment, Feature, Feedback Request, Feedback Response, Flight Plan, Incident, Key Result, Measure, Objective, Problem Record, Release Ticket, Requirement, Risk, Scenario, Service Asset, Service Catalog, Service Change, Service Operations Partner, Service Tuner, Shared Parameter, Shared Steps, Story, Task, Task Group, Test Case, Test Plan, Test Suite

### Primary Types for TWIG V1

| Type | State Model | Shorthand |
|------|-------------|-----------|
| Task | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Task Group | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Deliverable | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Feature | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Epic | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Scenario | Proposed → Committed → Started → Completed / Cut | `p/c/s/d/x` |
| Bug | Active → Resolved → Closed | `a/r/d\c` |

### Work Tree Hierarchy (Observed)

```
Epic
  └── Scenario / Feature
        ├── Deliverable
        │     ├── Task              (default child)
        │     └── Task Group        (situational)
        │           └── Task
        └── Bug
```

Tasks parent directly to Deliverables by default. Task Groups are used situationally for grouping related tasks. Bug is a sibling of Deliverable (parents to Scenario/Feature). Story is out of scope. Exact parent/child rules TBD (DI-003).
