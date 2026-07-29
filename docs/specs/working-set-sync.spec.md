# Working Set & Sync — Functional Specification

> Status: **Draft** — working session with Dan, 2026-04-27

## 1. Workspace Model

The workspace is the user's configurable view of relevant work items. It is the union
of **configured sources** (sprints, area paths) and **manual pins** (tracked items/trees),
minus **exclusions**.

### 1.1 Workspace Sources

Sources are **explicitly configured** — nothing is implicit. Each source contributes
items to the working set.

| Source | Configuration | Storage | Scope |
|--------|--------------|---------|-------|
| Sprint iteration | `twig workspace sprint add <path>` | `.twig/config` → `workspace.sprints[]` | Team-shared |
| Area path | `twig workspace area add <path>` | `.twig/config` → `defaults.areaPathEntries[]` | Team-shared |
| Tracked item | `twig workspace track <id>` | `.twig/tracking.json` (gitignored) | User-local |
| Tracked tree | `twig workspace track-tree <id>` | `.twig/tracking.json` (gitignored) | User-local |
| Excluded item | `twig workspace exclude <id>` | `.twig/tracking.json` (gitignored) | User-local |

**Key principle:** Sprint items are ONLY included when at least one sprint iteration
is explicitly added to the workspace. No implicit "current iteration" inclusion.

### 1.2 Sprint Iteration Management

#### Relative Iteration References

| Reference | Meaning |
|-----------|---------|
| `@Current` | The team's current sprint iteration (resolved from ADO) |
| `@Current-1` | Previous sprint |
| `@Current+1` | Next sprint |
| `@Current-N` | N sprints before current |
| `@Current+N` | N sprints after current |
| `Project\Sprint 5` | Absolute iteration path |

Relative references are **resolved at refresh time** — the config stores the literal
`@Current` string, and iteration resolution maps it to the actual path each time.

#### Commands

```
twig workspace sprint add @Current          # Add current sprint
twig workspace sprint add @Current-1        # Add previous sprint
twig workspace sprint add "Project\Sprint 5" # Add absolute iteration
twig workspace sprint remove @Current-1     # Remove a sprint
twig workspace sprint list                  # Show configured sprints
```

#### Init Integration

During `twig init` (interactive):
- Prompt: "Add the current sprint to your workspace?" → defaults to Yes
- If yes, adds `@Current` to `workspace.sprints[]`

During `twig init` (non-interactive):
- `--sprint @Current` flag adds the sprint
- `--area <path>` flag adds area path

### 1.3 Area Path Management

Moves from `twig area` to `twig workspace area`:

```
twig workspace area add <path> [--exact]    # Add area path (default: include children)
twig workspace area remove <path>           # Remove area path
twig workspace area list                    # Show configured area paths
twig workspace area sync                    # Import team area paths from ADO
```

Storage: `.twig/config` → `defaults.areaPathEntries[]` (unchanged location, already works).

### 1.4 Tracking (User-Local)

Tracked items and exclusions are **user-local** state that should not be shared
via config. They persist across DB rebuilds (which destroy SQLite tables).

Storage: `.twig/tracking.json` (gitignored)

```json
{
  "tracked": [
    { "id": 2115, "mode": "tree", "addedAt": "2026-04-27T10:00:00Z" },
    { "id": 2200, "mode": "single", "addedAt": "2026-04-27T10:05:00Z" }
  ],
  "excluded": [
    { "id": 2150, "addedAt": "2026-04-27T10:10:00Z" }
  ]
}
```

Commands (unchanged surface, new storage):

```
twig workspace track <id>           # Pin a single item
twig workspace track-tree <id>      # Pin an item + subtree
twig workspace untrack <id>         # Remove pin
twig workspace exclude <id>         # Hide from workspace view
twig workspace exclusions           # List excluded items
```

### 1.5 Tree Tracking — Sync Behavior

When a tree-tracked item is synced during refresh, it performs a multi-directional
fetch anchored on the root item:

```
           ┌─ grandparent
           │     (recursive UP — parents until root)
           ├─ parent
           │
  root ────┤── link target A ─╌╌ (stop, no further recursion)
  (tracked)├── link target B ─╌╌ (stop)
           │     (ONE level of links — successors, predecessors, related)
           │
           ├─ child 1
           │   ├─ grandchild 1a    (recursive DOWN — all descendants)
           │   └─ grandchild 1b
           └─ child 2
               └─ grandchild 2a
```

**Sync phases (in order):**

1. **Root** → fetch from ADO, save to cache
2. **Parents** (recursive up) → fetch each ancestor until root of hierarchy
3. **Children** (recursive down) → fetch entire subtree
4. **Root links** → fetch link targets (successors, predecessors, related)
   one level deep — these items are materialized into the cache
5. **Child/parent links** → store link *metadata* (source ID, target ID,
   link type) in `work_item_links` table, but do NOT fetch the linked items

**Auto-cleanup:** If a tree-tracked root item is "not found" in ADO (deleted),
it is automatically untracked.

**Single tracking** (`track` without `-tree`) only syncs the item itself —
no parent/child/link expansion. The item is fetched from ADO and saved on
each refresh.

### 1.6 Cleanup Policy

Tracked items can be auto-untracked based on the `tracking.cleanupPolicy` config:

| Policy | Behavior |
|--------|----------|
| `none` | Never auto-clean (default) |
| `on-complete` | Untrack when item state resolves to Completed category |
| `on-complete-and-past` | Untrack when completed AND in a past iteration |

### 1.5 Working Set Computation

The working set is computed fresh on each access:

```
WorkingSet = Union(
    ActiveItem + ParentChain + Children,    # Always (context-derived)
    SprintItems(configured iterations),      # Only if sprints configured
    AreaItems(configured area paths),        # Only if area paths configured
    TrackedItems + TrackedTreeDescendants,   # User-local pins
    DirtyItems,                              # Items with pending changes
    Seeds                                    # Local seeds (negative IDs)
) - ExcludedItems
```

**Sections** organize the workspace display (Sprint → Area → Recent → Manual)
with first-mode-wins deduplication.

---

## 2. Sync Model

### 2.1 Two Operations

| Operation | Command | Phases | When to Use |
|-----------|---------|--------|-------------|
| **Sync** | `twig sync` | Push (flush to ADO) + Pull (refresh from ADO) | After local edits |
| **Refresh** | `twig refresh` | Pull only | Update cache, no local changes |

### 2.2 Push Phase (Flush)

For each dirty item:
1. **Push notes first** via AddComment API, then clear *only* the note rows
   (type-scoped). Notes are additive ADO comments and cannot conflict with
   field-level metadata, so they must not be sequenced behind field conflict
   resolution — see step 3 and PolyphonyRequiem/twig#251.
2. If the item has field changes, fetch the remote item from ADO.
   **Conflict detection**: if `remote.revision > local.revision` → conflict.
   Notes-only items skip this entirely — there is nothing to fetch or compare.
3. **Conflict resolution**: prompt user (accept remote / merge / abort).
   Every outcome here concerns *field* changes only. Because notes were already
   pushed in step 1, an abort, an unresolved conflict, or an accept-remote can
   no longer strand or destroy a staged note.
4. PATCH to ADO with retry on concurrency conflict
5. Clear pending changes + dirty flag
6. Re-fetch item from ADO to update local revision

**Invariant:** Sync MUST NEVER lose local changes without explicit user consent.

> **Why notes go first (#251).** Notes used to be pushed *after* conflict
> resolution. Every early exit in that flow skipped the note push, and the
> accept-remote branch called a non-type-scoped `ClearChangesAsync` — deleting a
> staged note the user had never consented to discard. The user consented to
> abandoning their *field* changes; the note was collateral. That is a direct
> violation of the invariant above, which is why the ordering here is normative
> rather than incidental. Clearing notes must stay type-scoped
> (`ClearChangesByTypeAsync(id, "note")`) so an unresolved field conflict can
> neither strand the note nor cause a double-post on the next sync.

### 2.3 Pull Phase (Refresh)

1. Build WIQL query from configured sprints + area paths
2. Fetch matching items from ADO
3. **Protected items** (dirty OR have pending changes) are NEVER overwritten.
   There is no exception and no flag — wayfinder 0004 slice 5 deleted `--force`.
4. Save non-protected items to cache
5. Hydrate ancestor chain (up to 5 levels). Ancestor writes are protected too:
   until 0004 slice 5 this step used an unguarded batch save and clobbered
   staged edits on parent items on the DEFAULT path, with no flag involved.
6. Sync tracked trees (re-fetch subtrees)
7. Apply cleanup policy (auto-untrack completed items if configured)
8. Sync process types + field definitions

### 2.4 Read Command Refresh

Read commands (`show`, `status`, `tree`, `workspace`) perform a background
refresh after rendering only when `--refresh` is specified.

- default (no `--refresh`) → cached data only (fast, potentially stale; staleness is reported)
- Default → render from cache, then refresh in background

### 2.5 Dirty State Lifecycle

```
Mutation (state/update/note)
  → pending_changes row created
  → work_items.is_dirty = 1
  → item is "protected" during refresh

twig sync / twig save
  → flush pending changes to ADO
  → clear pending_changes rows
  → clear dirty flag
  → item is no longer protected

twig discard
  → clear pending_changes rows
  → clear dirty flag
  → NO push to ADO (changes lost)

(no bypass exists)
  → an item with pending_changes stays protected until those changes are
    flushed, discarded, or resolved. `--force` was deleted by 0004 slice 5:
    it overwrote dirty items while leaving pending_changes orphaned, and it
    also suppressed the conflict report as a side effect of emptying the
    protected set.
```

### 2.6 Stash

Git stash integration — NOT part of core sync model:
- `twig stash` → `git stash` with work item context in message
- `twig stash pop` → `git stash pop` + restore twig context from branch name

---

## 3. Init

`twig init` establishes the workspace connection and initial configuration.

### Interactive Mode
```
twig init
→ Prompt: Organization? → "dangreen-msft"
→ Prompt: Project? → "Twig"
→ Prompt: Team? → "" (default)
→ Detect process template from ADO
→ Prompt: Add current sprint to workspace? → Yes → adds @Current
→ Prompt: Add area paths? → Yes → runs area sync from ADO team settings
→ Create .twig/config
→ Create .twig/twig.db (schema v10)
→ Fetch process types, field definitions
→ Add .twig/ to .gitignore
→ Initial refresh
```

### Non-Interactive Mode
```
twig init <org> <project> [team] [--sprint @Current] [--area Twig]
→ Same setup, no prompts
```

### Post-Init State
- `.twig/config` — org, project, team, process, defaults (area/sprint), display, git
- `.twig/<org>/<project>/twig.db` — SQLite cache with schema, empty data
- `.twig/tracking.json` — empty (created on first track operation)
- `.gitignore` — `.twig/` entry added

---

## 4. Universal Command Requirements

Every twig CLI command MUST have:

1. **Complete help text** — `--help` produces:
   - One-line description
   - Full argument/option documentation with types and defaults
   - At least 2 usage examples (simple + advanced)
2. **`--output` format support** — `human`, `json`, `json-compact`, `minimal`
   (exceptions: pure-utility commands like `upgrade`, `web`)
3. **Consistent exit codes** — 0 = success, 1 = error, 2 = usage error
4. **Error formatting** — errors go through `fmt.FormatError()` for format-aware output
5. **Telemetry** — instrumented with allowed properties (command name, duration, exit code, format)
