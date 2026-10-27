# TWIG — Terminal Work Integration Gadget

A high-performance, opinionated CLI for Azure DevOps work item management. Twig brings a git-like interaction model to ADO work tracking: fast local reads from a SQLite cache, terse commands, context-aware operations, and seamless integration with developer workflows.

Built with C# .NET 10 Native AOT — single binary, sub-100ms cold start.

> **[Full Documentation](docs/README.md)** — architecture, internals, and contributor guides

## Quick Start

```bash
# Initialize (connects to your ADO org and project)
twig init --org contoso --project MyProject

# Sync data from ADO into local cache
twig sync

# View your assigned work items
twig ws
```

### Offline help and agent guidance

`twig --help` starts with common tasks; `twig <group> --help` narrows discovery
and `twig <command> --help` gives full syntax, examples, effects and failures.
`twig --help-all` retains the complete catalog.

`twig --skill` prints executable-matched generic guidance without a workspace or
authentication. `twig skills --help` covers explicit Hermes, OMP and Copilot CLI
installation, updates and user-companion selection. Only generic Twig skills
ship initially; integration/variant packages are separately supplied.
See [skill delivery and ownership](docs/features/skills.md).

## Working with Items

Twig uses an **active context** model — you set a work item as your focus, then all
commands operate on it until you change context.

```bash
# Set context by ID or title match
twig set 12345
twig set "auth bug"

# View the active item
twig status                     # full detail view
twig tree                       # hierarchical tree from active item

# Navigate the hierarchy
twig nav up                     # move to parent
twig nav down "subtask"         # move to child by title match
```

### Read-only native discussion pages

`twig bench discussion --stdin -o json` reads **one** remote ADO comments page
for an explicitly qualified cached member of a saved Bench. It never falls back
to the active item or current Bench, refreshes/enriches the cache, changes native
work/context/Bench/pending/journal data, posts a comment, or grants Save/Apply
authority. An admitted cold PAT runtime may attest its principal through the
existing authentication fence; this is not a work-item/metadata/mutation request.
Other providers fail closed before constructing the ADO discussion graph.

Close stdin after one strict version 1 request:

```json
{"version":1,"itemKey":{"checkoutRoot":"<native checkout root>","connectionId":"<native connection reference>","bindingId":"<native binding ID>","workItemId":42},"bench":"Sprint","expectBench":"7","expectIdentity":"<native identity ID>","cursor":null}
```

Use the admitted native qualified item key and expected saved Bench/identity,
not panel IDs or guessed local IDs. Input is bounded to 262144 UTF-8 bytes,
depth 32; duplicate/unknown members and trailing documents are errors. Output
supports JSON (one complete envelope), human and minimal, not `ids`.

Success has `status: "read"`, a `native/available` capability, exact native
context, comments and paging. Comment IDs/versions and paging/context counters
are decimal strings; the existing qualified key retains its integer
`workItemId`. Authors contain only nullable ID/display name; UTC timestamps,
deletion status, body and format preserve missing values as explicit `null`.
Empty body differs from unavailable body. Known formats are `markdown`/`html`;
an unfamiliar string is `unknown`. Body text is untrusted data. Human output
labels version, author ID/name, created/modified UTC timestamps, deletion, format
and body explicitly. Human/minimal strings are AOT-safe JSON-quoted text with
escaped terminal controls and markup, not rendered HTML/Markdown. Bare `null`
means unavailable; `""` means an empty string and `"null"` is literal text.
Minimal comment rows have nine tab-separated columns: ID, version, author ID,
author display name, createdAt, modifiedAt, isDeleted, format, body. Nullable
string cells use the same quoted/null distinction; isDeleted is `true`, `false`
or `null`. The final summary row is `discussion`, count, traversal and
`snapshot-not-guaranteed`. This quoting is not HTML/Markdown sanitization.
No identity/comment links, email/unique-name metadata, or rendered HTML are
exported.

Paging reports `scope: "provider-page"`, `traversal: "more"` or `"exhausted"`,
and `snapshotConsistency: "not-guaranteed"`. To request another page, pass the
returned `nextCursor` unchanged as `cursor` with the same explicit request
qualifiers. Its version, exact context and fixed option profile detect
accidental mismatches only: **the cursor is not authenticated or authority**.
Every invocation independently admits the item, Bench, origin and principal.
The provider token is untrusted paging data, percent-encoded only as a query
value; it cannot choose the origin, item, credential or endpoint. Native code
never follows `nextPage` or supplied identity/comment URLs, automatically walks
pages, or retries an invalidated context.

Twig adapter limits are 50 entries, 2 MiB of response bytes (bounded before
materialization), and 8192 UTF-8 bytes/no control characters per provider token.
These are **not provider limits**. Oversize or inconsistent pages are refused,
never truncated or reported as an empty/exhausted success. Exhaustion describes
this traversal, not a consistent whole-thread snapshot. Deleted comments are
requested explicitly; unavailable deleted content is never reconstructed.

The fixed GET uses comments `7.1-preview.4`, ascending order, `includeDeleted=true`
and `$expand=none`. The [Microsoft 7.1 schema](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/comments/get-comments?view=azure-devops-rest-7.1)
and [first-party generated interfaces](https://github.com/microsoft/azure-devops-node-api/blob/master/api/interfaces/WorkItemTrackingInterfaces.ts)
declare comment `id`; Learn examples instead show `commentId`. This boundary
uses the formal schema, refuses a missing valid `id`, and does not invent an alias.
That is source-contract evidence, **not live-wire proof**. Counts, deletion
visibility and continuation do not establish snapshot consistency.

Refusals return `status: "refused"`, `context: null`, `comments: []`,
`paging: null`, and a structured `error` without provider bodies, tokens or URLs.
JSON stderr is a separate formatted error. Exit 2 means usage/invalid JSON/input
size; exit 1 means an unavailable, denied, changed, canceled or invalid read;
exit 0 means a validated page, including a legitimately empty one. Failures
before command construction retain the existing CLI error path and may have no
discussion envelope. Missing stdout is not evidence of a successful empty read.
Semantic detail v1/v2 and history (updates, not discussion) are unchanged.

### Recoverable native drafts

`twig draft open 42 --bench Sprint -o json` opens the shared recoverable draft
and returns editing envelope **version 1**, separate from the unchanged detail
and draft inspectors. It explicitly inspects the native ADO form and rules but
does not refresh the item, switch the current Bench, publish changes, or grant
Save/Apply authority. Unsupported providers and fields are refused explicitly.
The ordered form and unavailable/read-only reasons remain visible.

For local staging, send one JSON request through `twig draft stage --stdin -o json`
and close stdin. The request contains `version: 1`, the **unchanged** `nativeContext`
from OPEN, its decimal-string `generation`, `baselineRevision`, `observedRevision`,
and an ordered `changes` array of `{ "fieldRef": "...", "proposedValue": null }`
or string values. `null` and `""` remain distinct. The UTF-8 input limit is
262144 bytes, with at most 128 changes; exceeding either limit is an error, never
truncation. Cancellation interrupts input; EOF is required before staging.

Evidence and draft writes share a SQLite transaction for normal-operation
commit/rollback. Evidence and drafts remain in separate attached WAL files;
cross-file crash or power-loss atomicity is **not** guaranteed. Inconsistent
evidence after a crash fails closed: explicitly reopen while retaining intent,
never automatically correct or rebase it. The editing envelope always reports
`committed`; refusals carry structured `error.code`, `message`, and `recovery`.
A post-commit origin fence failure reports `indeterminate` and `committed: true`:
reopen and inspect, never automatically retry or rebase. STAGE writes only the
recoverable draft, not pending changes or the server. Native publication
capabilities and process gates remain authoritative.

Configuration failures before command construction retain the existing CLI
error path and may have no editing envelope (for example, an unknown provider).
Hosts must treat missing or invalid stdout as an unknown outcome, not proof of
rollback, and must not automatically retry. JSON editing refusals that reach the
command emit the envelope on stdout and a formatted JSON error on stderr.

To retain exact intent locally, send `twig draft retain --stdin -o json` one
version 1 request containing **only** `version`, the unchanged `nativeContext`,
and decimal-string `generation`, `baselineRevision`, and `observedRevision` from
the latest editing envelope, then close stdin. The same 262144-byte UTF-8 and
depth-32 limits apply; duplicate/unknown members, rounded numbers and caller
authorizer fields are refused. Native code rechecks the exact draft, baseline,
origin, served evidence, process gates and explicit Bench. Empty or fully
restored-to-baseline intent is refused as `draft-empty`, without a journal row.

A successful `retained` envelope contains `capture`: the exact editing context
and decimal tokens, native `digest`, `operationId`, `canonicalJson` **string**,
and ordered `effects` with lossless null/empty values. The canonical native v1
proposal's numeric ADO targets/revisions stay inside that string. Its
`originCoverage: "editing-context-and-native-origin-digest"` is deliberately not
a full PlanOrigin projection or a semantic review. `authorized: false` and
`canApply: false` describe this receipt's lack of publication authority; they
do not promise the journal will remain Planned forever.

RETAIN uses the existing capture store and journal transaction, reusing an exact
capture only while it is Planned with no authorization or confirmation evidence.
`committed: true` includes unchanged exact reuse, not necessarily an insertion.
It never exports a file (an internal journal SourcePath is not evidence of a
file), flushes pending work, selects an active item, authorizes or applies.
Legacy `draft capture` export/recovery is unchanged. A failed fence after the
retention transaction reports `indeterminate`, `committed: true` and the capture
when available; inspect it rather than retrying automatically. Retention is not
publication authority, and an arbitrary authorizer string is not trusted proof.

Review a retained capture with `twig draft review <digest>`. Human output shows
each exact field effect with JSON-escaped nullable baseline and proposed values;
`-o minimal` emits the digest and effect count, while `-o json` returns the
closed review envelope. Review reads only the stored journal/capture and current
local evidence; it does not read or create a proposal file, alter export state or
journal audit fields, authorize, or apply. Freshness means the captured intent
matches available cached native evidence during validation, not a remote read or
a lease. Legacy captures without served-metadata evidence, malformed or
inconsistent captures, non-capture digests, and stale local evidence are refused.
The cache and durable journal are attached databases; review uses the existing
non-deferred transaction on the shared connection, but does not claim cross-file
crash atomicity or a single remote observation instant.

### Navigation

`twig nav` opens an interactive tree navigator — browse the hierarchy with arrow keys,
jump to linked items, and select a new context without knowing IDs. Subcommands let you
move directly:

```bash
twig nav                        # interactive tree navigator
twig nav up                     # move to parent item
twig nav down                   # pick a child interactively
twig nav down "auth"            # jump to child matching "auth"
twig nav next                   # move to next sibling
twig nav prev                   # move to previous sibling
twig nav back                   # go back to previous context
twig nav fore                   # go forward (undo a back)
twig nav history                # show recent navigation history
```

> **Shorthand:** `up`, `down`, `next`, and `prev` also work without the `nav` prefix — e.g. `twig up` is the same as `twig nav up`.

### Declared policy assessment and repairs

Native proposal enforcement remains in place. To inspect existing noncompliance, name the
published items explicitly:

```bash
twig policy assess --ids 42,43 -o json
twig policy reconcile --ids 42 --evidence <digest> --finding f1 --strategy remove-labels -o json --proposal-only > repair.json
twig proposal preview --file repair.json -o json
```

Choose the finding and strategy from the assessment; parent targets and retained labels or
dependency edges are never guessed. Assessment reports cached evidence, unavailable checks
and bounded alternatives, not live or whole-board certification.
Dependency scope includes incident cached sources in both directions; contradictory
reciprocal observations remain unavailable. Parent checks do not certify out-of-scope children.
A repair only authors existing native proposal operations: review, confirmation,
authorization and journaled apply
remain separate. Refresh and reassess after apply; successful writes alone do not prove
compliance. See the [policy contract](docs/specs/declared-policy-decisions.spec.md#current-state-assessment-and-reconciliation)
for supported strategies, evidence limits and exit codes.

### Native full-screen TUI

On Windows and Linux, `twig tui` opens the native Bench browser in-process, with
the same workspace, authority checks and terminal lifecycle as `twig bench browser`.
Tree, Table, local editing and read-only proposal Review share one application:

```bash
twig tui --cwd /path/to/workspace --view tree
twig-tui --cwd /path/to/workspace --view table
twig-bench-tui --cwd /path/to/workspace --view tree
```

The release archive contains identical NativeAOT executable bytes under `twig`,
`twig-tui` and `twig-bench-tui` (with `.exe` on Windows), plus the platform SQLite
native library. Standalone names select the TUI by executable basename; they are
not subprocess wrappers or separate UI builds. Windows aliases carry the exact
signed primary image when release signing is configured. The Windows/Linux Herdr
host is `twig-herdr` and launches this same native application directly, preserving
its action and pane IDs.

macOS keeps its existing `twig tui` / `twig-tui` Terminal.Gui companion and legacy
Go/Herdr paths. `Twig.Tui`, shared domain/infrastructure sources and their required
dependencies remain for macOS; they do not represent a second Windows/Linux UI.
See the [TUI command guide](docs/commands/experimental/tui.md) for platform behavior
and the [Bench browser guide](docs/commands/bench/README.md) for controls and trust.

### Mutating Work Items

```bash
# Change state (prefix matching — "act" matches "Active", "do" matches "Done")
twig state active
twig state done

# Update fields — short names work for common fields
twig update title "New title"
twig update description "Rich content" --format markdown

# Fully qualified names work too (better for scripts or disambiguation)
twig update System.Title "New title"
twig update Microsoft.VSTS.Scheduling.StoryPoints 5

# Add a discussion note
twig note "Investigated the issue, root cause is in auth middleware"
```

Changes are pushed to ADO immediately on `update`, `state`, and `note`. No manual save step.

### Process-Agnostic Design

Twig doesn't assume you're using Agile, Scrum, CMMI, or Basic. On first sync, it discovers your project's process template from ADO and learns the available work item types, states, and fields automatically. State prefix matching (`twig state do` → "Done") works regardless of your process — twig resolves against whatever states your process actually defines.

## Views

Twig has three views for different perspectives on your work:

### `twig ws` — Your Working Set

Shows items assigned to you in the current sprint. This is your daily driver — what's on your plate right now.

```
Workspace
──────────────────────────────────────────────────────────────────
  Active: #1618 □ Issue — MCP Server (Epic 1484) Closeout Findings [Done]

 ID      Type      Title                                          State
 #1645   □ Issue   Release pipeline and installer scripts         Doing
 #1644   □ Issue   Self-updater companion extraction              Done
 #1618   □ Issue   MCP Server closeout findings                   Done
 #1603   ◆ Epic    Follow up on closeout findings                 To Do

 Seeds (2):  #s1 Add TUI publish step  ·  #s2 Verify installer checksum
 ✎ 1 dirty item
```

### `twig tree` — Hierarchy from Active Item

Shows the parent chain above and children below the active item. Useful for understanding where a work item sits in the backlog structure.

```
◆ Follow Up on Closeout Findings [To Do]
├── ● □ #1618 MCP Server (Epic 1484) Closeout Findings [Done]
│   ├── │ □ #1633 Add explicit work-item ID flag to twig update command [Done]
│   ├── │ □ #1620 Add pre-close-out sync step for pending notes [Done]
│   ├── │ □ #1622 Add task-level state verification gate before Issue closure [Done]
│   ├── │ □ #1621 Add worktree-aware close-out flow [Done]
│   └── │ □ #1619 Enforce branch naming consistency in PR group manager [Done]
└── ...10
```

The `●` marker indicates the active (focused) item. Parent items appear above, children below. The `...10` shows there are 10 sibling items collapsed.

### `twig sprint` — Full Team Sprint

Shows the entire sprint grouped by assignee — useful in standups or to see team workload at a glance.

```
Sprint
──────────────────────────────────────────────────────────────────
  Active: #1618 □ Issue — MCP Server (Epic 1484) Closeout Findings [Done]

 Daniel Green (4 items)
   #1645   □ Issue   Release pipeline and installer scripts   Doing
   #1644   □ Issue   Self-updater companion extraction         Done
   #1618   □ Issue   MCP Server closeout findings              Done
   #1603   ◆ Epic    Follow up on closeout findings            To Do

 Alex Kim (2 items)
   #1650   □ Issue   Query command returns stale results       Doing
   #1648   □ Issue   Cache invalidation on team change         New
```

All three views read from the local cache, so they render instantly even with hundreds of items.

## Seeds — Local Work Item Drafts

Seeds are work items that exist only in your local cache — they haven't been created in ADO yet. This lets you plan and structure work offline, then publish when you're ready.

When you create a seed, twig assigns it a **negative ID** (e.g. `#-1`, `#-2`) to distinguish it from real ADO items. Seeds are children of the active item and appear in your workspace view alongside real items.

```bash
# Create a seed (child of the active item)
twig seed new "Implement auth middleware"
# → creates local-only item #-1 under the active work item

# Edit a seed before publishing
twig seed edit -1

# View all seeds
twig seed view
```

### Seed Chains — Ordered Task Sequences

`twig seed chain` creates a sequence of seeds linked with **successor relationships**, so they form an ordered execution plan. This is useful when breaking an issue into tasks that should be done in a specific order.

```bash
# Create a chain of linked seeds interactively
twig seed chain
# → prompts for each title; each seed is linked as a successor of the previous one
# → result: #-1 → #-2 → #-3 (successor chain)
```

### Publishing Seeds

When you're happy with the plan, publish seeds to ADO. Twig creates real work items in dependency order, replacing negative IDs with real ADO IDs and preserving all links.

```bash
# Publish a single seed
twig seed publish -1

# Publish all seeds in dependency order
twig seed publish --all

# Publish all and link each new work item to a branch (best-effort)
twig seed publish --all --link-branch feature/my-branch
```

## How Data Stays in Sync

Twig keeps a local SQLite cache of your sprint data so reads are instant (~1ms).
Here's what to expect:

- **Reads are local** — `status`, `tree`, `ws` never wait on the network. You always get a fast response from the cache.
- **Writes go straight to ADO** — `update`, `state`, and `note` push immediately. The cache updates after ADO confirms.
- **Stale data refreshes automatically** — when you `twig set` an item, twig checks if the cached copy is recent. If it's older than a few minutes, it quietly fetches the latest from ADO first.
- **`twig sync` forces a full refresh** — pulls the latest sprint data from ADO, flushing any pending changes first.
- **Your local edits are protected** — if you have unsaved local changes on an item, a sync will never overwrite them.
- **Conflicts are handled** — if someone else edited the same item, twig re-fetches the latest version and retries your change automatically.

For the full technical details on the sync pipeline, caching tiers, and conflict resolution, see the [Data Layer](docs/architecture/data-layer.md) architecture doc.

## Shell Prompt Integration

Twig writes a `.twig/prompt.json` file with the current context (active work item ID, title, type, state) after every context change. Your shell prompt can read this file for instant, zero-latency status display — no subprocess needed.

This works with any prompt framework that can read a JSON file:

- **[Oh My Posh](https://ohmyposh.dev/)** — use a `text` segment reading `$TWIG_PROMPT` (see `twig ohmyposh init`)
- **[Starship](https://starship.rs/)** — use a `custom` command with `cat .twig/prompt.json | jq -r .title`
- **[Powerlevel10k](https://github.com/romkatv/powerlevel10k)** — custom instant prompt segment

## Output Formats

All commands support `--output human|json|minimal`:

- **human** — ANSI-colored with type badges, state colors, and contextual hints
- **json** — machine-readable, no ANSI escapes (pipe to `jq` or AI agents)
- **minimal** — plain text for scripting and `grep`

## Installation

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.ps1 | iex
```

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/twig/main/install.sh | bash
```

### Update

```bash
twig upgrade
```

Installs to `~/.twig/bin/` and adds it to your PATH. The installer is idempotent — safe to run again to reinstall or repair.

Windows and Linux installers require the three same-image native entry points
from the downloaded archive; `twig tui` does not require a legacy companion or
fall back to one. Apple Silicon macOS still installs the legacy `twig-tui`
companion alongside the native CLI. Intel macOS release builds remain unavailable.

### Verifying downloads

Every release publishes a SHA256 hash next to each archive plus an aggregate `SHA256SUMS` manifest. If you download manually (rather than via `install.ps1` / `install.sh`), you can verify the archive against its hash before extracting:

**Linux:**

```bash
curl -fsSLO https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-linux-x64.tar.gz
curl -fsSLO https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-linux-x64.tar.gz.sha256
sha256sum -c twig-linux-x64.tar.gz.sha256
```

**macOS:**

```bash
curl -fsSLO https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-osx-arm64.tar.gz
curl -fsSLO https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-osx-arm64.tar.gz.sha256
shasum -a 256 -c twig-osx-arm64.tar.gz.sha256
```

**Windows (PowerShell):**

```powershell
Invoke-WebRequest https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-win-x64.zip -OutFile twig-win-x64.zip
Invoke-WebRequest https://github.com/PolyphonyRequiem/twig/releases/latest/download/twig-win-x64.zip.sha256 -OutFile twig-win-x64.zip.sha256
$expected = (Get-Content twig-win-x64.zip.sha256).Split(' ')[0]
$actual = (Get-FileHash -Algorithm SHA256 twig-win-x64.zip).Hash.ToLower()
if ($expected -ne $actual) { throw "Hash mismatch" } else { "OK: $actual" }
```

The aggregate `SHA256SUMS` file on each release lists every asset's hash; `sha256sum -c SHA256SUMS` (or `shasum -a 256 -c SHA256SUMS`) verifies all of them in one shot once the matching archives are present in the working directory.

## Prerequisites

- An Azure DevOps account with access to the target organization and project, explicitly enrolled with [`twig auth login` or `twig auth pat`](docs/commands/system/auth-login.md) and selected through a connection binding. Azure CLI is not required for normal CLI authentication.

## Build from Source

Requires the SDK pinned in [`global.json`](global.json) (currently a .NET 11 preview).

```bash
dotnet build
python tools/verify-build.py --timeout 15
dotnet publish src/Twig -r win-x64 -c Release
```

The build and validation are separate gates. The offline smoke checks the actual
CLI's startup/help, source-generated JSON, current store/binding admission,
proposal preview, and wrong-account refusal with a hard 15-second deadline; it
never builds or runs test hosts. Use narrowly scoped regression checks for the
proposed changeset when needed. Routine CI and pre-push use this smoke; complete
platform validation runs on release candidates before signing or publication.

For a smoke deadline investigation, add `--trace <new-file.jsonl>` to preserve flushed
worker/child boundaries outside captured output. Trace rows contain only fixed phase/event
labels, process-relative elapsed seconds and exit codes, never command arguments, environment
or work-item payloads. CI uploads these boundaries even on failure. This is diagnostic
evidence, not a larger timeout or a correction for a latency failure. The deterministic
harness regression is `python tools/test-verify-build.py`.

Preservation migration is an explicit internal capability, not an automatic
workspace upgrade or a new user-facing migration command. It retains local work,
context and navigation, requires restore-qualified workspace-local backups under
`.twig/backups`, and refuses unclassified data rather than discarding originals.
CI qualifies native host-inventory migration and retained-publication recovery on
Windows; macOS lacks that admission and Linux can refuse inaccessible `/proc` handles.
Normal cross-platform gates remain; these checks never authorize existing-store migration.

`SqliteBackupPathProbeTests` is an opt-in `Integration` diagnostic, excluded from
routine and release-candidate eligibility. Select it explicitly only to investigate
a host's native SQLite path-failure behavior; a different failure signature is not
evidence that preservation regressed. Portable preservation checks run separately.

## Next Topics

- **[Architecture Overview](docs/architecture/overview.md)** — how twig is built: layered design, project structure, key constraints
- **[Data Layer](docs/architecture/data-layer.md)** — sync mechanics, caching tiers, conflict resolution in depth
- **[Commands](docs/architecture/commands.md)** — full command catalog, rendering pipeline, telemetry
- **[ADO Integration](docs/architecture/ado-integration.md)** — REST client, authentication, link management
- **[MCP history](docs/architecture/mcp-server.md)** — withdrawn from current delivery; retained as redesign context
- **[Build & Release](docs/architecture/build-and-release.md)** — AOT compilation, versioning, release pipeline

## License

[MIT](LICENSE)
