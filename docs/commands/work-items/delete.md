---
command: delete
group: work-items
summary: Permanently delete a work item from Azure DevOps.
stability: stable
mutates: ado
---

# `twig delete`

Delete a work item from Azure DevOps. This is **permanent and
irreversible** — prefer `twig state Closed` for anything you might want
back. `delete` requires an explicit ID (there is no "delete the active
item" shortcut), refuses seeds and linked items, and gates the deletion
behind an interactive confirmation that the caller must type `yes` to.

## Synopsis

```
twig delete <id> [--force] [-o <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `<id>` | yes | Work item ID to delete. |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--force` | bool | `false` | Skip the interactive confirmation prompt. Required for non-interactive callers. |
| `-o`, `--output <format>` | `human` \| `json` \| `minimal` | `human` | Output format. |

## Behavior

Sequence (see `src/Twig/Commands/DeleteCommand.cs:79-158`):

1. **Resolve the item.** `ActiveItemResolver.ResolveByIdAsync` looks in the
   cache first, then ADO, so a bad ID reports a clean "not found" without
   any destructive side effect. The error message specifically points at
   `twig state Closed` as the reversible alternative.
2. **Seed guard.** If the resolved item is a seed, `delete` refuses and
   directs you to `twig seed discard <id>`. Seeds don't exist in ADO;
   there is nothing for `delete` to remove.
3. **Fresh fetch and link guard.** `DeleteWorkflow.PrepareAsync` fetches
   the item again from ADO and checks its link count. Any link — parent,
   child, related, artifact — blocks the delete with a message listing
   the link totals and (again) redirecting to `twig state Closed`. Remove
   every link before retrying (`src/Twig/Commands/DeleteCommand.cs:97-115`).
4. **Confirmation.** Unless `--force`, twig prints the item's ID, title,
   type, and state, then a warning that the action is permanent, then
   reads a line from stdin. The caller must type `yes` (case-insensitive,
   trimmed). Anything else exits `0` with "cancelled" — no destructive
   call is made. In non-interactive mode (redirected stdout) the
   confirmation prompt would hang, so twig refuses without `--force`
   entirely (`src/Twig/Commands/DeleteCommand.cs:118-143`).
5. **Delete.** `DeleteWorkflow.ExecuteAsync` writes an audit record, calls
   `IAdoWorkItemService.Delete`, removes the item from the local cache,
   and refreshes prompt state.

The pre-flight and the destructive call are separated on purpose: the ADO
DELETE is only reached after every guard passes. `--force` skips only the
confirmation prompt — the seed guard, the link guard, and the fresh fetch
still run.

## Examples

Delete a leaf item interactively:

```
$ twig delete 1234

  ID:    #1234
  Title: Fix login redirect
  Type:  Task
  State: To Do

⚠ This action is PERMANENT. Consider 'twig state Closed' instead — it preserves history and is reversible.

Type 'yes' to confirm deletion: yes
Deleted #1234 Fix login redirect.
```

Force-delete in a script (still runs the seed and link guards):

```
$ twig delete 1234 --force
Deleted #1234 Fix login redirect.
```

Blocked because the item has links:

```
$ twig delete 1234
Cannot delete #1234 'Fix login redirect' — it has 3 link(s): 1 parent, 2 children. Remove all links before deleting. Consider 'twig state Closed' instead — it preserves history and is reversible.
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Item deleted | Exit `0`. |
| Confirmation declined | Exit `0`, "cancelled" message. |
| Missing `<id>` | Exit `2` (framework usage error). |
| Item not found (cache and ADO) | Exit `1`. |
| Item is a seed | Exit `1`, redirects to `twig seed discard`. |
| Item has links (any direction) | Exit `1`, redirects to `twig state Closed`. |
| Non-interactive caller without `--force` | Exit `1`. |
| Fresh fetch for delete failed | Exit `1`. |
| ADO delete call failed | Exit `1`. |

## See also

- [`twig state Closed`](state.md) — reversible alternative that preserves
  history.
- [`twig discard`](discard.md) — clear local staged changes, not the
  work item itself.
- `twig seed discard` — remove an unpublished seed instead of `delete`.
