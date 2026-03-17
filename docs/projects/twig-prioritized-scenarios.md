# Twig Prioritized Scenarios — Individual Contributor Focus

Prioritized for an IC managing their own work items, optimizing for both human UX
and toolchain/scripting integration. Tiers represent implementation priority.

---

## Tier 1 — Daily Driver (core loop an IC repeats dozens of times a day)

### 1.1 See my work
- **My assigned items** — default view shows MY items, current sprint, grouped by state
- **Active context** — always know which item I'm "on" right now
- **Stale data indicator** — know how fresh my local view is

### 1.2 Navigate fast
- **Jump by ID** — `twig go 12345` or `twig 12345`
- **See item in context** — parent chain + children (tree view)
- **Auto-detect context from git branch** — `feature/12345-title` sets active item

### 1.3 Change state quickly
- **Shorthand transitions** — `twig start`, `twig done`, `twig close` (category-based, not state-name-based)
- **State from anywhere** — `twig done 12345` without switching context first

### 1.4 Quick edits
- **Change title** — `twig update title "new title"`
- **Change assignment** — `twig update assign "someone"` or `twig take` (assign to self)
- **Stage locally** — edits accumulate, push when ready
- **Review pending** — see what I've changed before pushing

### 1.5 Sync
- **Pull** — `twig refresh` fetches latest for my sprint/scope
- **Push** — `twig save` sends all pending changes upstream
- **Auto-stale warning** — commands warn when data is old

### 1.6 Pipe-friendly output
- **JSON output** — `--format json` on every command, structured for `jq`
- **Minimal output** — `--format minimal` for scripting (IDs only, no chrome)
- **Exit codes** — meaningful codes for scripting (0=success, 1=error, 2=conflict)
- **Stderr for errors, stdout for data** — clean separation

---

## Tier 2 — Weekly Workflow (used regularly but not every hour)

### 2.1 Notes & comments
- **Add a comment** — `twig note "blocked on API access"` (pushed on save)
- **View discussion** — see comment thread on an item

### 2.2 Quick capture
- **Create child task** — `twig add task "write unit tests"` under current context
- **Create sibling** — `twig add story "new acceptance flow"` at same level
- **Minimal creation** — just type + title, everything else defaults

### 2.3 Sprint awareness
- **Current sprint view** — what's in my iteration, what state is each thing in
- **Sprint progress** — rough completion feel (X of Y done)
- **Move to next sprint** — `twig move 12345 --next` for replanning

### 2.4 Conflict resolution
- **Detect on push** — tell me if remote changed since I pulled
- **Interactive resolve** — pick local/remote/abort per field
- **Discard local** — `twig discard` to throw away my unpushed edits

### 2.5 Git integration
- **Branch → item** — auto-set context from branch name pattern
- **Commit message helper** — `twig commit-msg` outputs `AB#12345` for hooks
- **Open in browser** — `twig open` launches ADO web UI for current item

### 2.6 Shell prompt
- **Prompt segment** — output active item info for Oh My Posh / Starship / PS1
- **Fast** — must complete in <50ms (read from cache, no network)

---

## Tier 3 — Power User (weekly/monthly, adds depth)

### 3.1 Richer views
- **View description** — render rich text / markdown in terminal
- **View attachments list** — know what's attached
- **View history** — changelog of who changed what
- **View links** — related, predecessor, successor items
- **Diff local vs remote** — see what changed since last pull

### 3.2 Editor integration
- **Edit description in $EDITOR** — `twig edit desc` opens vim/code with the description
- **Edit acceptance criteria** — same pattern for AC field

### 3.3 Filtering & search
- **Filter by state** — `twig ws --state active`
- **Filter by type** — `twig ws --type bug`
- **Filter by tag** — `twig ws --tag "P1"`
- **Keyword search** — `twig search "login timeout"` across cached titles

### 3.4 Hierarchy manipulation
- **Reparent** — move an item under a different parent
- **View full subtree** — all descendants recursively

### 3.5 Advanced editing
- **Change iteration** — `twig update iteration "Sprint 42"`
- **Change area path** — `twig update area "Platform\Auth"`
- **Add/remove tags** — `twig tag add "P1"`, `twig tag rm "P1"`
- **Batch edit** — `twig ws --state active --set state=Closed` (with confirmation)

### 3.6 Bookmarks & recents
- **Recent items** — `twig recent` shows last N items I visited
- **Bookmarks** — `twig pin 12345`, `twig pins` to list

---

## Tier 4 — Nice to Have (depth features, built on Tier 1-3 foundation)

### 4.1 Interactive TUI
- Full-screen tree + detail split view
- Keyboard-driven vim/arrow navigation
- Inline field editing
- Context switching without quitting

### 4.2 Reporting
- **Status summary** — counts by state/type (for standup prep)
- **Aging** — items stuck in a state too long
- **Export** — CSV / markdown table dump

### 4.3 Advanced sync
- **Selective sync** — only pull items matching a query
- **Background sync** — daemon/cron-style periodic refresh
- **Sync scope** — `twig refresh --subtree 12345`
- **Offline queue visibility** — `twig pending` shows queued operations

### 4.4 Templates & cloning
- **Create from template** — `twig add --template bug-report`
- **Clone item** — `twig clone 12345 --title "variant B"`

### 4.5 Multi-project
- Multiple ADO projects in one workspace
- Per-project config and cache

### 4.6 Security
- Credential rotation handling (token expiry re-auth flow)
- Local cache encryption at rest

---

## Toolchain Integration Principles

These apply across all tiers:

1. **Every command supports `--format json|minimal|human`** — no exceptions
2. **Stdout is data, stderr is diagnostics** — always, even in human mode
3. **Exit code contract** — 0 success, 1 error, 2 conflict, 3 not-initialized
4. **Single-item commands accept ID as argument OR use active context** — `twig state done` (active) vs `twig state done 12345` (explicit)
5. **List commands support `--filter` predicates** — composable with shell pipelines
6. **No interactive prompts in non-TTY** — detect pipe/redirect and fail fast with clear error instead of hanging
7. **Config as code** — `.twig/config` is JSON, version-controllable, documented schema
8. **Fast startup** — <100ms for cached reads, no lazy framework init
9. **Idempotent operations** — running the same command twice produces the same result
10. **Git-friendly mental model** — pull/stage/push, local-first, explicit sync points
