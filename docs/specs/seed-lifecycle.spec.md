# Seed Lifecycle Specification

> **Domain:** Seed creation, editing, linking, validation, publishing, reconciliation
> **Status:** Draft
> **Epic:** #2169

## Overview

Seeds are **local-only draft work items** stored in the same SQLite `work_items` table
as ADO items, distinguished by negative IDs and `IsSeed = true`. They enable offline
composition of work-item trees that are later published atomically to Azure DevOps.

The seed lifecycle is: **create → edit → link → validate → publish → reconcile**.

---

## 1. Invariants

These are the structural guarantees the seed system must enforce. Every command
in this spec must preserve all invariants or explicitly document why it defers
enforcement (e.g., to publish time).

### 1.1 Identity Invariants

| ID | Invariant | Enforcement Point |
|----|-----------|-------------------|
| I-1 | Seed IDs are always negative integers | `SeedFactory.Create()` — thread-safe `Interlocked.Decrement` |
| I-2 | Seed IDs never escape to ADO field values | Publish-time validation (new) |
| I-3 | `IsSeed` flag is immutable after creation | Domain model — no public setter |
| I-4 | ID mapping (`oldId → newId`) is recorded atomically with publish | Transactional publish |

### 1.2 Hierarchy Invariants

| ID | Invariant | Enforcement Point |
|----|-----------|-------------------|
| H-1 | Parent must be published (positive ID) before child can publish | `seed publish` guard |
| H-2 | Discarding a parent cascades to all child seeds | `seed discard` (changed — was warn-only) |
| H-3 | Child seeds' `ParentId` is remapped atomically during parent publish | Transactional remap |
| H-4 | Orphaned `ParentId` references are repaired by `seed reconcile` | Reconcile flow |

### 1.3 Link Invariants

| ID | Invariant | Enforcement Point |
|----|-----------|-------------------|
| L-1 | Virtual links require at least one seed (negative ID) endpoint | `seed link` guard |
| L-2 | Link type must be a valid `SeedLinkTypes` member | `seed link` / `seed unlink` |
| L-3 | (sourceId, targetId, linkType) is unique | SQLite UNIQUE constraint |
| L-4 | **No cycles in the link graph** | `seed link` — eager detection, hard reject |
| L-5 | Links are remapped (old → new ID) atomically during publish | Transactional remap |
| L-6 | Link promotion to ADO is non-fatal — publish succeeds, warnings shown | `seed publish` |

### 1.4 Publish Invariants

| ID | Invariant | Enforcement Point |
|----|-----------|-------------------|
| P-1 | Only unpublished seeds (`IsSeed=1`, `Id < 0`) can be published | `seed publish` guard |
| P-2 | Validation is enforced unless `--force` | `seed publish` |
| P-3 | Local update (remap + delete + insert) is transactional | SQLite transaction |
| P-4 | Batch publish uses topological sort (Kahn's algorithm) | `seed publish --all` |
| P-5 | Deterministic tiebreaker: oldest `SeedCreatedAt` first | Topological sort |
| P-6 | Batch publish validates the full dependency graph before starting | Pre-flight check (new) |

### 1.5 Mutation Invariants

| ID | Invariant | Enforcement Point |
|----|-----------|-------------------|
| M-1 | `twig update` and `twig state` work on seeds via shared mutation provider | Mutation routing (new) |
| M-2 | Seed mutations are local-only — no ADO API calls | Mutation provider |
| M-3 | Field edits via `seed edit` use external editor; `twig update` uses inline args | Command layer |

---

## 2. Domain Model

### 2.1 Storage

Seeds share the `work_items` table with ADO items:

| Column | Seed Behavior |
|--------|---------------|
| `id` | Negative integer (from `Interlocked.Decrement` counter) |
| `is_seed` | `1` (always) |
| `parent_id` | Nullable; negative (seed parent) or positive (ADO parent) |
| `seed_created_at` | `DateTimeOffset` — creation timestamp for ordering |

Supporting tables:

| Table | Purpose |
|-------|---------|
| `seed_links` | Virtual links between seeds (and seed ↔ ADO items) |
| `publish_id_map` | Maps old negative ID → new positive ADO ID |

### 2.2 Virtual Link Types

| Type | Reverse | ADO Promotion |
|------|---------|---------------|
| `parent-child` | — | `System.LinkTypes.Hierarchy-Forward` |
| `blocks` | `blocked-by` | `System.LinkTypes.Dependency-Forward` |
| `blocked-by` | `blocks` | `System.LinkTypes.Dependency-Reverse` |
| `depends-on` | `depended-on-by` | `System.LinkTypes.Dependency-Reverse` |
| `depended-on-by` | `depends-on` | `System.LinkTypes.Dependency-Forward` |
| `related` | — | `System.LinkTypes.Related` |
| `successor` | `predecessor` | `System.LinkTypes.Dependency-Forward` |
| `predecessor` | `successor` | `System.LinkTypes.Dependency-Reverse` |

Directional links (blocks/depends-on/successor) have explicit reverse types.
`related` and `parent-child` have no auto-reverse.

### 2.3 Validation Rules

Rules are loaded from `.twig/seed-rules.json` (or defaults):

```json
{
  "requiredFields": ["System.Title"],
  "requireParent": false
}
```

**Default rules:** Only `System.Title` required, no parent required.

---

## 3. Commands

### 3.1 `seed new [title] [--type <type>] [--editor]`

Create a new local seed work item.

**Behavior:**
1. Resolve parent context (active item or explicit)
2. Validate `--type` against process config; infer from parent if omitted
3. Allocate negative ID via `SeedFactory.Create()`
4. If `--editor`: open external editor for field editing
5. Persist to `work_items` table
6. Set as active context

**Exit codes:** 0 success, 1 invalid type or parent unresolvable.

**Telemetry:** `seed_new` — `had_editor`, `had_type`, `had_parent`, `exit_code`, `duration_ms`.

### 3.2 `seed edit <id>`

Open a seed in the external editor for field modification.

**Behavior:**
1. Load seed by ID; guard `IsSeed`
2. Serialize fields to editor format
3. Open `$EDITOR` (or system default)
4. Parse editor output; apply field changes
5. Persist updated seed

**Exit codes:** 0 success, 1 seed not found or not a seed.

**Telemetry:** `seed_edit` — `field_count`, `exit_code`, `duration_ms`.

### 3.3 `seed view`

Display all seeds grouped by parent.

**Behavior:**
1. Load all seeds from `work_items` where `is_seed = 1`
2. Group by `parent_id`
3. Render tree using Spectre.Console

**Exit codes:** 0 always.

**Telemetry:** `seed_view` — `seed_count`, `exit_code`, `duration_ms`.

### 3.4 `seed chain [--parent <id>] [--type <type>] <titles...>`

Batch-create seeds with automatic successor links between them.

**Behavior:**
1. Resolve parent context
2. Validate type (if specified)
3. For each title:
   - Create seed via `SeedFactory.Create()`
   - Persist
   - If not first: create `successor` link from previous → current
4. Display chain summary

**Invariants enforced:** Type validation, parent resolution, auto-successor linking.

**Exit codes:** 0 success, 1 type/parent invalid.

**Telemetry:** `seed_chain` — `seed_count`, `had_type`, `had_parent`, `exit_code`, `duration_ms`.

### 3.5 `seed link <sourceId> <targetId> [--type <type>]`

Create a virtual link between two items (at least one must be a seed).

**Behavior:**
1. Validate at least one ID is negative (L-1)
2. Normalize and validate link type (L-2)
3. **Eager cycle detection** — load full link graph, add proposed edge, check for cycles (L-4)
4. If cycle detected: **reject with error** — do not create the link
5. Check cache for positive IDs (warn if missing, but allow)
6. Insert into `seed_links` table (L-3 enforced by UNIQUE constraint)

**Exit codes:** 0 success, 1 both IDs positive / invalid type / cycle detected / duplicate.

**Telemetry:** `seed_link` — `had_cycle`, `exit_code`, `duration_ms`.

### 3.6 `seed unlink <sourceId> <targetId> [--type <type>]`

Remove a virtual link.

**Behavior:**
1. Normalize and validate link type
2. Delete from `seed_links` where match
3. Silent success if link doesn't exist

**Exit codes:** 0 always (idempotent).

**Telemetry:** `seed_unlink` — `exit_code`, `duration_ms`.

### 3.7 `seed links [id]`

List virtual links, optionally filtered by seed ID.

**Behavior:**
1. Query `seed_links` (all or filtered by ID)
2. Display with source, target, type, and resolution status

**Exit codes:** 0 always.

**Telemetry:** `seed_links` — `link_count`, `exit_code`, `duration_ms`.

### 3.8 `seed validate [id]`

Validate seeds against publish rules.

**Behavior:**
1. Load rules from `.twig/seed-rules.json` (or defaults)
2. If `id`: validate single seed; otherwise validate all
3. Check: `System.Title` non-empty, required fields present, parent requirement
4. Report pass/fail per seed

**Exit codes:** 0 all pass, 1 any validation failure.

**Telemetry:** `seed_validate` — `seed_count`, `pass_count`, `fail_count`, `exit_code`, `duration_ms`.

### 3.9 `seed publish [id | --all] [--force] [--dry-run] [--link-branch <branch>]`

Publish seeds to Azure DevOps.

#### Single Publish

1. Guard: ID must be negative and `IsSeed` (P-1)
2. Guard: `ParentId` must be positive (H-1)
3. Validate unless `--force` (P-2)
4. If `--dry-run`: return plan, no API calls
5. Create in ADO via `IAdoWorkItemService.CreateAsync()`
6. Fetch back full item from ADO
7. **Transactional local update** (P-3):
   - Record mapping in `publish_id_map` (I-4)
   - Remap `seed_links` IDs (L-5)
   - Remap `ParentId` in child seeds (H-3)
   - Delete old seed row
   - Insert new ADO item row
8. Promote seed links to ADO relations (L-6 — non-fatal)
9. If `--link-branch`: link published items to branch via artifact link
10. Best-effort cache refresh
11. If active context was this seed: auto-update to new ID

#### Batch Publish (`--all`)

1. Load all seeds and seed_links
2. **Pre-flight dependency validation** (P-6, new):
   - Verify all seeds in the graph are publishable
   - Verify all parent references resolve (or are in the batch)
   - Report unpublishable seeds before starting
3. Build dependency graph; topological sort with cycle detection (P-4)
4. Cyclic seeds reported and skipped
5. Publish each seed in topological order (oldest first on ties — P-5)

#### `--link-branch` (Decoupled)

The `--link-branch` flag creates ADO artifact links to a git branch. It is
**decoupled from local git context** — the repo GUID is resolved by querying
the ADO project's repository list using the `--repo` name (or a configured
default). The repo name is matched against the ADO project's Git repositories.

```
twig seed publish --all --link-branch feature/my-branch --repo my-repo
```

If `--repo` is omitted, the configured default repository name is used
(from `.twig/config` or workspace settings). If the repo name cannot be
resolved to a GUID, a warning is emitted and branch linking is skipped
(non-fatal).

**Exit codes:** 0 all published, 1 any failure (validation, ADO error, cycle).

**Telemetry:** `seed_publish` — `seed_count`, `published_count`, `cycle_count`,
`link_warning_count`, `had_force`, `had_dry_run`, `had_link_branch`,
`exit_code`, `duration_ms`.

### 3.10 `seed discard <id> [--yes]`

Discard a seed and all its descendant seeds.

**Behavior:**
1. Load seed; guard `IsSeed`
2. **Find all descendant seeds** — traverse `ParentId` chain recursively (H-2)
3. Display discard plan (seed + N descendants)
4. Prompt for confirmation unless `--yes`
5. Delete all `seed_links` referencing any discarded ID
6. Delete all descendant seed rows
7. Delete target seed row

**Invariant change (H-2):** Previously, discarding a parent left children orphaned.
Now discard cascades to the entire subtree. This prevents broken `ParentId` references.

**Exit codes:** 0 success (including user-declined), 1 seed not found or not a seed.

**Telemetry:** `seed_discard` — `cascade_count`, `exit_code`, `duration_ms`.

### 3.11 `seed reconcile`

Repair orphaned references left by partial publishes or external changes.

**Behavior:**
1. Load all `seed_links` and `publish_id_map`
2. For each link:
   - Both endpoints exist → keep
   - One stale + has mapping → remap
   - One stale + no mapping → remove (orphaned)
3. For each seed with negative `ParentId`:
   - Parent exists → keep
   - Parent has mapping → remap `ParentId`
   - Parent missing + no mapping → warn (parent was discarded)
4. Display reconciliation report

**Exit codes:** 0 always (advisory).

**Telemetry:** `seed_reconcile` — `links_repaired`, `links_removed`,
`parent_ids_fixed`, `warning_count`, `exit_code`, `duration_ms`.

---

## 4. Cross-Cutting Behavior

### 4.1 Unified Mutation Provider (New — M-1)

`twig update` and `twig state` currently only operate on ADO items. The spec
requires a **shared mutation provider** that routes mutations based on the
active item's identity:

| Active Item | `twig update` | `twig state` |
|-------------|---------------|--------------|
| ADO item (positive ID) | Queue ADO field update | Queue ADO state transition |
| Seed (negative ID) | Apply field change locally | Apply state change locally |

The mutation provider implements a common interface (`IMutationProvider`) with
two backing implementations:
- `AdoMutationProvider` — queues changes for ADO sync
- `SeedMutationProvider` — writes directly to local SQLite

Command layer routes to the correct provider based on `item.IsSeed`. The user
experience is identical — `twig update System.Title "New Title"` works the same
regardless of whether the active item is a seed or ADO item.

### 4.2 Navigation

- `twig set <id>` works with both positive and negative IDs
- Navigation commands (`up`, `down`, `next`, `prev`) traverse seeds using
  `ParentId` and `seed_links` (successor/predecessor)
- `back` / `fore` resolve published IDs via `publish_id_map`

### 4.3 Sync

- `twig sync` skips seeds entirely — they are local-only
- Seeds do not appear in dirty item detection
- Seeds are not included in ADO push/pull operations

### 4.4 Show

- `twig show <id>` accepts negative seed IDs
- Displays seed fields with `[SEED]` indicator
- `--tree` flag shows seed children under their parent

---

## 5. Known Limitations

These are accepted trade-offs, not bugs:

| Limitation | Rationale |
|------------|-----------|
| Link promotion is non-fatal | ADO item already exists; can't roll back creation. Links can be added manually. |
| Cache refresh after publish is best-effort | Non-critical; next sync repairs. |
| `seed-rules.json` has no schema validation | Low priority; rules are simple key-value. |
| Positive ID targets in links aren't validated against ADO | Would require network call at link time; deferred to publish. |
| `related` links are unidirectional in storage | Matches ADO behavior; reverse is implicit. |

---

## 6. Removals

| Flag/Command | Reason | Replacement |
|--------------|--------|-------------|
| `seed publish --link-branch` (git-context form) | Git integration removal | Decoupled form: resolve repo GUID from ADO project + `--repo` name |

---

## 7. MCP Tool Parity

| CLI Command | MCP Tool | Notes |
|-------------|----------|-------|
| `seed new` | `twig_seed_new` | |
| `seed edit` | — | Requires external editor; not MCP-compatible |
| `seed view` | `twig_seed_view` | |
| `seed chain` | `twig_seed_chain` | |
| `seed link` | `twig_seed_link` | Includes cycle detection |
| `seed unlink` | `twig_seed_unlink` | |
| `seed links` | `twig_seed_links` | |
| `seed validate` | `twig_seed_validate` | |
| `seed publish` | `twig_seed_publish` | |
| `seed discard` | `twig_seed_discard` | |
| `seed reconcile` | `twig_seed_reconcile` | |

`twig update` and `twig state` MCP tools (`twig_update`, `twig_state`) automatically
route to seed mutations when the active item is a seed — no separate MCP tools needed.
