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

## Prerequisites

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (for authentication)

## Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet test
dotnet publish src/Twig -r win-x64 -c Release
```

## Next Topics

- **[Architecture Overview](docs/architecture/overview.md)** — how twig is built: layered design, project structure, key constraints
- **[Data Layer](docs/architecture/data-layer.md)** — sync mechanics, caching tiers, conflict resolution in depth
- **[Commands](docs/architecture/commands.md)** — full command catalog, rendering pipeline, telemetry
- **[ADO Integration](docs/architecture/ado-integration.md)** — REST client, authentication, link management
- **[MCP Server](docs/architecture/mcp-server.md)** — AI agent integration via the twig-mcp tool server
- **[Build & Release](docs/architecture/build-and-release.md)** — AOT compilation, versioning, release pipeline

## License

[MIT](LICENSE)
