# Twig Flow — Developer Inner Loop Lifecycle

> **Date**: 2026-03-16
> **Status**: DRAFT
> **Category**: Scenario Document

---

## Overview

This document defines the **opinionated developer workflow** for Twig, exposed as the `twig flow` command group. `twig flow` orchestrates primitives (`set`, `state`, `save`, `refresh`) into a prescriptive lifecycle: **start** → work → **done** → review → **close**. It codifies git branch management, ADO state transitions, assignment, sync safety, and save scoping into a single coherent workflow.

The flow commands compose existing primitives — they never bypass them. Every operation `twig flow` performs can also be done manually with the underlying commands. The flow is the happy path; the primitives are the escape hatch.

---

## Personas

| Persona | Description | Primary flow |
|---------|-------------|-------------|
| **IC Developer** | Individual contributor picking up sprint work | `flow start` → code → `flow done` → `flow close` |
| **IC with existing branch** | Developer who already created a branch manually | `flow start --no-branch` |
| **CI/Automation** | Pipeline step that transitions state | `flow start 12345 --format json --no-branch --no-assign` |
| **Multi-tasker** | Developer switching between items | `flow start` (new) implicitly pauses current |

---

## Command Surface

### `twig flow start [id-or-pattern]`

Begins work on an item. Sets context, transitions state, assigns to self, creates a git branch.

### `twig flow done [id]`

Finishes work. Saves pending changes for the active work tree, transitions state to resolved, offers PR creation.

### `twig flow close [id]`

Closes the item post-merge. Guards against open PRs and unsaved changes, transitions to closed, cleans up branch, clears context.

---

## Detailed Flows

### `twig flow start [id-or-pattern]`

#### With explicit ID or pattern

```
$ twig flow start 12345

  ◆ #12345 [Bug] Login timeout on slow connections
  
  ✓ Context set to #12345
  ✓ State: New → Active
  ✓ Assigned: → Dan Green
  ✓ Branch: feature/12345-login-timeout (created, checked out)
```

**Sequence:**

1. **Resolve item** — delegates to `SetCommand` logic. If numeric ID, check cache then fetch from ADO. If pattern, fuzzy match with disambiguation.
2. **Fetch latest from ADO** — get remote revision to ensure we're working with current data.
3. **Conflict check** — if item is dirty locally with `remote.Revision > local.Revision`, run `ConflictResolutionFlow`. If user aborts, halt.
4. **Set active context** — `contextStore.SetActiveWorkItemIdAsync(id)`.
5. **Transition state** — resolve current state's category via `StateCategoryResolver`. If `Proposed`, transition to first `InProgress` state using `StateShorthand.Resolve('c', stateEntries)`. If already `InProgress` or beyond, skip (idempotent). State change is pushed to ADO immediately (same as `twig state c`).
6. **Assign to self** — if unassigned, assign to authenticated user (from `profiles/me` display name). If assigned to someone else, skip unless `--take` is passed. Assignment is pushed to ADO immediately.
7. **Create & checkout branch** — run `git rev-parse --is-inside-work-tree` to detect git. If in a repo, generate branch name from configurable template and `git checkout -b {name}`. If uncommitted changes exist on current branch, warn and require `--force` to proceed. If not in a git repo, skip with info message.
8. **Print summary** — list all actions taken.

#### With no argument (interactive picker)

```
$ twig flow start

  Pick an item to start:
  
  > ◆ #12345 [Bug] Login timeout on slow connections       [New]
    ▪ #12346 [Task] Write retry configuration docs          [New]
    ● #12347 [User Story] Dashboard performance             [New]
  
  (↑/↓ to select, Enter to confirm, Esc to cancel)
```

**Behavior:**
- Query `workItemRepo` for items in current sprint assigned to me (or unassigned) in `Proposed` state category.
- Show interactive `SelectionPrompt` (Spectre path when TTY) or error with list (non-TTY).
- After selection, proceed with full start sequence.
- If no items in `Proposed`: `"No unstarted items in current sprint."`

#### Flags

| Flag | Effect |
|------|--------|
| `--no-branch` | Skip git branch creation/checkout |
| `--no-state` | Skip state transition |
| `--no-assign` | Skip self-assignment |
| `--take` | Assign to self even if already assigned to someone else |
| `--force` | Proceed even with uncommitted git changes |
| `--format json\|minimal\|human` | Output format (non-interactive in json/minimal) |

#### Context switch behavior

If an active context already exists when `twig flow start <new-id>` is called:
- The previous context is simply replaced (no implicit save or state change on the old item).
- Hint: `"Switched from #111 to #12345. Previous item left in current state."`
- Rationale: implicit saves/transitions on the *old* item are dangerous — the user may not want that side effect.

---

### `twig flow done [id]`

#### Default (active context)

```
$ twig flow done

  ◆ #12345 [Bug] Login timeout on slow connections
  
  ✓ Saved 2 item(s): #12345 (1 field), #12348 (1 note)
  ✓ State: Active → Resolved
  ? Create pull request? [Y/n] y
  ✓ PR #891 created: feature/12345-login-timeout → main
    https://dev.azure.com/org/project/_git/repo/pullrequest/891
```

**Sequence:**

1. **Resolve target** — if `id` provided, use that item. Otherwise, use active context. Error if no active context.
2. **Save active work tree** — get active item + its dirty children. For each with pending changes: fetch remote → `ConflictResolutionFlow` → patch → clear pending → refresh cache. If any conflict is unresolvable (user aborts), halt `flow done` entirely.
3. **Transition state** — move from `InProgress` to first `Resolved` state (`StateShorthand.Resolve('s', stateEntries)`). If item's type has no `Resolved` category (some process templates), use first `Completed` state (`'d'`). State change pushed to ADO immediately.
4. **Offer PR creation** — if current git branch has commits ahead of target branch (configurable, default `main`), prompt: `"Create pull request? [Y/n]"`. On `y`, create PR via ADO Git API with `AB#12345` in description. On `n` or non-TTY, skip.
5. **Print summary**.

#### With explicit ID

```
$ twig flow done 12345
```

- Uses item #12345 regardless of active context.
- Does NOT change active context.
- Saves pending changes for #12345 only (not active work tree — the explicit ID narrows scope).

#### Flags

| Flag | Effect |
|------|--------|
| `--no-save` | Skip the auto-save step |
| `--no-pr` | Skip the PR creation offer |
| `--format json\|minimal\|human` | Output format |

---

### `twig flow close [id]`

#### Default (active context)

```
$ twig flow close

  ◆ #12345 [Bug] Login timeout on slow connections
  
  ✓ PR #891: Completed (merged)
  ✓ State: Resolved → Closed
  ✓ Branch feature/12345-login-timeout deleted
  ✓ Context cleared
```

**Sequence:**

1. **Resolve target** — active context or explicit ID.
2. **Guard: unsaved changes** — if pending changes exist for this item, refuse: `"Unsaved changes on #12345. Run 'twig save' first."` Exit 1.
3. **Guard: open PR** — query ADO for PRs where source branch matches item's branch pattern. If active (not merged) PR exists:
   ```
   ⚠ Open PR #891 found: feature/12345-login-timeout → main
     Status: Active (not merged)
     https://dev.azure.com/org/project/_git/repo/pullrequest/891
   
   Close anyway? [y/N] N
   Cancelled. Merge or complete the PR first.
   ```
   Non-TTY: exit code 2 with PR details in output, no prompt.
4. **Transition state** — move to first `Completed` state (`StateShorthand.Resolve('d', stateEntries)`). If already `Completed`, skip.
5. **Delete branch** — if current branch matches item's branch pattern, offer: `"Delete branch feature/12345-login-timeout? [Y/n]"`. On `y`: `git checkout main && git branch -d {branch}`. On `n`, skip.
6. **Clear context** — `contextStore.SetActiveWorkItemIdAsync(null)`.
7. **Print summary**.

#### Flags

| Flag | Effect |
|------|--------|
| `--force` | Bypass PR guard and unsaved changes guard |
| `--no-branch-cleanup` | Skip branch deletion |
| `--format json\|minimal\|human` | Output format |

---

## Sync Safety Model

### Principles

1. **Refresh is atomic** — `twig refresh` either updates everything or nothing. If any dirty item would be overwritten by a remote change, the entire refresh halts.
2. **Save scopes to the work tree** — `twig save` pushes changes for the active item and its dirty children. `twig save --all` pushes everything.
3. **Flow commands auto-save before transitions** — `twig flow done` saves the active work tree before changing state.
4. **Flow commands auto-refresh the target item** — `twig flow start` and `twig flow done` fetch the latest revision before acting.
5. **Revision is the ETag** — `WorkItem.Revision` tracks the ADO revision number. `PatchAsync` sends it as `If-Match`. The server returns 412 on conflict.

### Refresh dirty guard

When `twig refresh` fetches a batch from ADO:

```
For each remote item in the fetched batch:
  local = cache lookup by ID
  if local exists AND local has pending changes OR is_dirty:
    if remote.Revision > local.Revision:
      → CONFLICT: remote changed AND local is dirty
      → Add to conflict list
    else:
      → Safe: remote hasn't changed, dirty state is coherent
      → Skip (don't overwrite, nothing new from remote)

If conflict list is non-empty:
  → Print all conflicting items with revision details
  → Exit 1 (entire refresh halts)
  → Suggest: "Run 'twig save' first, or 'twig refresh --force' to discard local changes."

If no conflicts:
  → SaveBatchAsync as normal (skip items where remote.Revision == local.Revision for efficiency)
```

### Save scoping

| Command | Scope | Behavior |
|---------|-------|----------|
| `twig save` | Active work tree | Active context item + children of active item that have pending changes |
| `twig save <id>` | Single item | Just that explicit item |
| `twig save --all` | All dirty | Every item with entries in `pending_changes` table |
| `twig workspace save` | All dirty | Same as `--all` (future, part of workspace rethink) |

For each item saved:
1. Fetch remote (`adoService.FetchAsync`)
2. Conflict resolution (`ConflictResolutionFlow.ResolveAsync`)
3. Patch ADO (`adoService.PatchAsync` with `expectedRevision`)
4. Push notes (`adoService.AddCommentAsync`)
5. Clear pending changes (`pendingChangeStore.ClearChangesAsync`)
6. Refresh cache (`adoService.FetchAsync` → `workItemRepo.SaveAsync`)

### SyncGuard service

Single point of truth for "which items are protected from overwrite":

```csharp
/// <summary>
/// Identifies items that must not be silently overwritten during refresh.
/// An item is protected if it has is_dirty=1 OR has rows in pending_changes.
/// </summary>
public static class SyncGuard
{
    public static async Task<IReadOnlySet<int>> GetProtectedItemIdsAsync(
        IWorkItemRepository repo, IPendingChangeStore pendingStore, CancellationToken ct = default)
    {
        var dirtyItems = await repo.GetDirtyItemsAsync(ct);
        var pendingIds = await pendingStore.GetDirtyItemIdsAsync(ct);
        var result = new HashSet<int>();
        foreach (var item in dirtyItems) result.Add(item.Id);
        foreach (var id in pendingIds) result.Add(id);
        return result;
    }
}
```

Used by `RefreshCommand`, and available for any future command that batch-updates cache.

---

## Git Integration

### Branch name template

Config key: `git.branchTemplate`

| Token | Value | Example |
|-------|-------|---------|
| `{id}` | Work item ID | `12345` |
| `{type}` | Work item type, lowercased | `bug` |
| `{title}` | Title, slugified | `login-timeout-on-slow` |

Default: `feature/{id}-{title}`

**Slugification rules:**
- Lowercase
- Spaces and underscores → hyphens
- Strip non-alphanumeric (except hyphens)
- Collapse consecutive hyphens
- Truncate to 50 characters
- Trim trailing hyphens

### Branch detection (passive)

Config key: `git.branchPattern` (regex, default: `(?:^|/)(?<id>\d{3,})(?:-|/|$)`)

On any command where no active context is set:
1. Run `git rev-parse --abbrev-ref HEAD`
2. Apply regex → extract work item ID
3. If ID exists in cache, emit hint: `"Tip: branch matches #12345. Run 'twig set 12345' to set context."`
4. **Never auto-set** — only hint. `twig flow start` is the intentional action.

### Git safety

| Scenario | Behavior |
|----------|----------|
| Not in a git repo | Skip all git operations, warn once |
| Uncommitted changes | `flow start` warns, requires `--force` to switch branches |
| Branch already exists | `git checkout {branch}` (no `-b`), warn: "Branch already exists, checking out" |
| Branch delete after close | Only if current branch matches pattern; prompt Y/n |
| Detached HEAD | Skip branch operations, warn |

---

## Output Contract

### Human format (default, TTY)

```
$ twig flow start 12345

  ◆ #12345 [Bug] Login timeout on slow connections
  
  ✓ Context set to #12345
  ✓ State: New → Active
  ✓ Assigned: → Dan Green
  ✓ Branch: feature/12345-login-timeout (created, checked out)
```

### JSON format

```json
{
  "command": "flow start",
  "itemId": 12345,
  "title": "Login timeout on slow connections",
  "type": "Bug",
  "actions": {
    "contextSet": true,
    "stateChanged": { "from": "New", "to": "Active" },
    "assigned": { "to": "Dan Green" },
    "branch": { "name": "feature/12345-login-timeout", "created": true }
  },
  "exitCode": 0
}
```

### Minimal format

```
feature/12345-login-timeout
```

Emits only the branch name (for `$(twig flow start 12345 --format minimal)` capture in scripts).

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Error (item not found, ADO failure, conflict aborted) |
| 2 | Guarded (open PR on close, dirty items on refresh) |

---

## Configuration

```json
{
  "git": {
    "branchTemplate": "feature/{id}-{title}",
    "branchPattern": "(?:^|/)(?<id>\\d{3,})(?:-|/|$)",
    "defaultTarget": "main"
  },
  "flow": {
    "autoAssign": "if-unassigned",
    "autoSaveOnDone": true,
    "offerPrOnDone": true
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `git.branchTemplate` | string | `feature/{id}-{title}` | Branch name template with tokens |
| `git.branchPattern` | string | `(?:^\|/)(?<id>\d{3,})(?:-\|/\|$)` | Regex to extract work item ID from branch name |
| `git.defaultTarget` | string | `main` | Target branch for PR creation and ahead/behind detection |
| `flow.autoAssign` | string | `if-unassigned` | `always`, `if-unassigned`, or `never` |
| `flow.autoSaveOnDone` | bool | `true` | Auto-save active work tree before `flow done` state transition |
| `flow.offerPrOnDone` | bool | `true` | Offer PR creation when branch is ahead of target |

---

## User Stories

### US-01: Start working on a known item

> **As** a developer starting work,
> **I want** to run `twig flow start 12345`
> **So that** my context is set, state is active, I'm assigned, and I'm on a working branch.

**AC:** Set context → transition Proposed→InProgress → assign if unassigned → create+checkout branch → print summary. Idempotent on state and assignment.

### US-02: Start working — pick from sprint

> **As** a developer starting a new task,
> **I want** `twig flow start` with no argument to show my unstarted sprint items
> **So that** I can pick and start in one step.

**AC:** Query Proposed-category items in current sprint → interactive picker → full start sequence.

### US-03: Finish work on current item

> **As** a developer who finished coding,
> **I want** `twig flow done` to save my changes, resolve the item, and offer a PR
> **So that** my work is fully wrapped up.

**AC:** Auto-save active work tree → transition InProgress→Resolved → offer PR if ahead of target → print summary. If type has no Resolved category, use Completed.

### US-04: Close after merge

> **As** a developer whose PR was merged,
> **I want** `twig flow close` to close the item and clean up
> **So that** my workspace is ready for the next item.

**AC:** Guard against unsaved changes → guard against open PRs (prompt if active, exit 2 in non-TTY) → transition to Completed → delete branch (prompt) → clear context.

### US-05: Done on a specific item

> **As** a developer who fixed a quick issue on a non-active item,
> **I want** `twig flow done 12345` to resolve that item without affecting my active context.

**AC:** Save pending changes for #12345 only → transition state → don't change active context.

### US-06: Start with selective flags

> **As** a developer who already has a branch,
> **I want** `twig flow start 12345 --no-branch` to skip branch creation.

**AC:** `--no-branch`, `--no-state`, `--no-assign` are combinable. Each skips its step.

### US-07: Non-interactive flow

> **As** a CI script,
> **I want** `twig flow start 12345 --format json` to work without prompts.

**AC:** No prompts in non-TTY. JSON output with structured action results. Exit code 0/1.

### US-08: Configurable branch names

> **As** a developer on a team with branch conventions,
> **I want** to configure the branch template.

**AC:** `git.branchTemplate` with `{id}`, `{type}`, `{title}` tokens. Title slugified per rules.

### US-09: Passive branch detection

> **As** a developer on an existing branch,
> **I want** Twig to hint that my branch matches a work item
> **So that** I can set context without remembering the ID.

**AC:** On commands with no active context, check branch name → regex match → hint with item ID. Never auto-set.

### US-10: Conflict during flow done

> **As** a developer pushing changes in `twig flow done`,
> **I want** conflicts handled the same way as `twig save`.

**AC:** Delegates to `ConflictResolutionFlow`. If aborted, halt `flow done`. JSON mode emits conflict JSON, exit 1.

### US-12: Refresh stops on dirty conflicts

> **As** a developer with unsaved local edits,
> **I want** `twig refresh` to stop entirely when remote updates conflict with dirty local items
> **So that** I never get a half-updated cache.

**AC:** Before SaveBatchAsync, check protected IDs via SyncGuard. If remote.Revision > local.Revision AND item is protected → CONFLICT. Print conflicting items, exit 1. `--force` overrides.

### US-13: Save scopes to active work tree

> **As** a developer,
> **I want** `twig save` to push changes for my active item and its dirty children.

**AC:** `twig save` = active item + dirty children. `twig save <id>` = single item. `twig save --all` = all pending changes. Each item gets conflict resolution. Summary with item counts.

### US-14: Flow guards against dirty state

> **As** a developer running `twig flow done`,
> **I want** all pending changes saved before the state transition.

**AC:** `flow done` auto-saves active work tree. If conflict unresolvable, halt. `flow close` refuses if unsaved changes exist.

---

## Dependencies

| Dependency | Status | Required by |
|------------|--------|-------------|
| `SetCommand` | Built | US-01, US-02 |
| `StateCommand` + `StateShorthand` | Built | US-01, US-03, US-04 |
| `SaveCommand` | Built (needs scope change) | US-03, US-13, US-14 |
| `ConflictResolutionFlow` | Built | US-10, US-12 |
| `StateCategoryResolver` | Built | US-01, US-03, US-04 |
| `profiles/me` identity | Built | US-01 (auto-assign) |
| `SyncGuard` service | **Not built** | US-12 |
| Git process wrapper | **Not built** | US-01, US-04, US-06, US-08, US-09 |
| ADO PR query API | **Not built** | US-04 (close guard) |
| ADO PR create API | **Not built** | US-03 (PR offer, stretch) |
| `RefreshCommand` dirty guard | **Not built** | US-12 |
| `SaveCommand` work tree scoping | **Not built** | US-13 |
| Config schema: `git.*`, `flow.*` | **Not built** | US-08, US-06 |
| Rendering consolidation plan | In progress | Shared DI prerequisite |

---

## Open Questions

| ID | Question | Proposed answer |
|----|----------|-----------------|
| OQ-001 | Should `flow start` auto-refresh if cache is stale? | No — explicit `twig refresh` first. Flow start only fetches the target item's latest revision, not the full sprint. |
| OQ-002 | What if `flow done` encounters a 412 (optimistic concurrency) from ADO during state patch? | Retry once after re-fetching revision. If still 412, surface as conflict. |
| OQ-003 | Should `flow close` auto-delete remote branches? | No — only local. Remote branch deletion is an ADO Git API call with higher blast radius. |
| OQ-004 | Should `flow start` on an already-InProgress item still create a branch? | Yes — the user explicitly asked to start work. They may have done `twig state c` manually earlier and now want the branch. |
| OQ-005 | What happens if `git checkout -b` fails (branch name collision)? | Check out existing branch instead of creating. Warn: "Branch already exists, checking out." |
