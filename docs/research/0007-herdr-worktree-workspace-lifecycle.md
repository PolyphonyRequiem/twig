# 820 - 0007 - What does herdr actually support for worktree and workspace lifecycle?

Herdr exposes four worktree verbs — `list`, `create`, `open`, `remove` — not the three this ticket assumed. The load-bearing findings: **a worktree cannot be adopted into an existing workspace through the worktree verbs, but a tab can trivially point into another checkout and herdr does not record that as membership**; **`worktree remove` has no liveness guard and destroys live panes without `--force`**; and **closing the last tab leaves the checkout on disk by design**, which exactly explains the observed orphans.

**Provenance caveat.** The installed binary is `herdr 0.8.2-preview.2026-08-19-b5c4a0176e91` (`herdr --version`, [verified-by-execution]). Source claims below were read against the released `v0.8.2` tag. Preview-build drift from that tag is possible and was not ruled out.

Execution evidence was gathered in a throwaway `/tmp` git repository with its own scratch worktrees and workspaces, all destroyed afterwards. Twig's 33 worktrees were untouched.

## 1. Adopting an existing worktree into an existing workspace

**Two paths, opposite answers.**

**(a) Refused via the worktree verbs.** `worktree create` and `worktree open` both start from the repo parent workspace and always yield their own workspace. `App::worktree_source_metadata` rejects a linked-worktree workspace with code `linked_worktree_source`. [read-from-source: `src/app/worktrees.rs:worktree_source_metadata`; `src/app/api/worktrees.rs:handle_worktree_open`/`resolve_worktree_source`]

```
$ herdr worktree open --workspace w19 --path /tmp/.../wt-feature --no-focus
{"error":{"code":"linked_worktree_source",
          "message":"New and open worktree actions start from the repo parent workspace."}}
```
[verified-by-execution]

`worktree open` creates a *new* workspace for a target checkout when it is not already open, via `create_workspace_with_options(entry.path…)`. [read-from-source: `src/app/api/worktrees.rs:handle_worktree_open`; `src/app/api/worktrees/deferred.rs:handle_api_worktree_add_finished`]

**Do not overclaim this as "always its own workspace."** `worktree open` MAY return an **already-open** workspace when the target checkout already carries explicit membership (`open_workspace_idx_for_checkout`). What it cannot do is place a target under an *arbitrary* existing workspace, and a linked-worktree source workspace is rejected outright. The precise statement is: **create and open yield a separate workspace unless that same checkout is already open.** [read-from-source: `src/app/api/worktrees.rs:open_workspace_idx_for_checkout`]

**(b) Succeeds via the tab verb.** herdr does not police tab cwd.

```
$ herdr tab create --workspace w19 --cwd /tmp/.../wt-feature --label adopted --no-focus
→ pane w19:p2, tab w19:t2, cwd = the SECOND worktree, inside the FIRST worktree's workspace
```
[verified-by-execution]

`handle_tab_create` accepts a `cwd: PathBuf` and simply calls `ws.create_tab` — there is **no git or worktree-membership check anywhere in the path**. That is the mechanism behind operational tab adoption. [read-from-source: `src/app/api/tabs.rs:handle_tab_create`]

**(c) The critical caveat.** After (b), `herdr worktree list` still reported that checkout as `open_workspace_id=NONE`. [verified-by-execution] Membership is an explicit `Workspace.worktree_space` populated by herdr's own worktree operations; tab creation only sets a cwd, and `WorktreeInfo.open_workspace_id` derives from explicit membership, not from any tab's location. [read-from-source: `src/workspace.rs:WorktreeSpaceMembership`/`worktree_space`; `src/app/api/worktrees.rs:worktree_info_for_entry` ~lines 529–547, which sets `open_workspace_id` via `open_workspace_idx_for_checkout`; `open_workspace_idx_for_checkout` ~line 569+ checks explicit membership and git identity, never an arbitrary tab cwd]

So **operational tab-level adoption exists, but is invisible to `worktree list` and therefore to any reaping signal built on it.** [inference from the source facts plus execution]

## 2. `worktree remove`

`worktree remove --workspace <id>` runs `git -C <repo_root> worktree remove [--force] <checkout>`, then calls `close_removed_linked_worktree_workspace` and emits `WorkspaceClosed`. It does not stop at git metadata. [read-from-source: `src/worktree.rs:build_worktree_remove_command`; `src/app/api/worktrees/deferred.rs:start_api_worktree_remove`/`handle_api_worktree_remove_finished`]

Executed against **live** workspace w19 holding two tabs with running panes:

```
$ herdr worktree remove --workspace w19
{"id":"cli:worktree:remove","result":{"forced":false,
  "path":"/home/…/.herdr/worktrees/repo/scratch-wt",
  "type":"worktree_removed","workspace_id":"w19"}}
```

It succeeded **without `--force`**, removed the checkout, closed workspace w19, and destroyed tab `t2` — whose cwd was a *different* worktree — as collateral. [verified-by-execution]

**There is no liveness guard**, in source or in observed behaviour. [read-from-source + verified-by-execution]

`--force` is passed only to git. It affects git's refusal on a dirty, modified, or untracked checkout; it is **not** a herdr workspace or liveness override. [read-from-source: `src/worktree.rs:build_worktree_remove_command`/`is_dirty_worktree_remove_error`; `deferred.rs` dirty precheck]

> This is the most consequential safety finding for ticket 0006. The reaping rule cannot rely on herdr refusing to remove something in use, because it does not refuse.

## 3. A checkout vanishing underneath its workspace

After removing a checkout outside herdr with `git worktree remove --force`:

- `herdr worktree list` **silently omitted the row** — no broken entry, no error.
- `herdr workspace list` **still reported the workspace alive**, `label=scratch3`, `tabs=1`.

[verified-by-execution]

Source lists current git porcelain entries and maps them; it does not reconcile or close workspaces when an entry disappears, while workspace state separately retains `worktree_space`. [read-from-source: `src/app/api/worktrees.rs:handle_worktree_list`; `src/worktree.rs:list_existing_worktrees`; `src/workspace.rs:WorktreeSpaceMembership`]

Broken references therefore persist silently. [inference]

## 4. `is_prunable`

`ExistingWorktree.is_prunable` initialises to `false` and becomes `true` only when a line beginning `prunable` appears in `git worktree list --porcelain`; herdr passes it straight through to `WorktreeInfo.is_prunable`. [read-from-source: `src/worktree.rs:parse_worktree_list_porcelain`/`list_existing_worktrees`; `src/api/schema/worktrees.rs:WorktreeInfo`. A source test parses `prunable stale` as true.]

This is **git's own prunable marker** — stale or missing checkout metadata — **not a herdr workspace-orphan marker**. [inference: the parser consumes git porcelain and performs no workspace lookup]

It is therefore **not usable** as a signal for "workspace has no tabs" or "lineage orphan". Observed directly: a deliberately orphaned worktree reported `open_workspace_id=NONE` and `is_prunable=false` simultaneously. [verified-by-execution]

Worse for reaping: `worktree_info_for_membership` (~lines 552–566) **hardcodes `is_prunable: false`** for any worktree with explicit membership. So the field is not merely uninformative about orphaning — for the membership path it is a constant. [read-from-source: `src/app/api/worktrees.rs:worktree_info_for_membership`]

## 5. Does closing the last tab remove the worktree?

**No — and this is designed behaviour, not a bug.**

```
$ herdr tab close w1A:t1
→ workspace w1A absent from `workspace list`
→ checkout still on disk
→ `worktree list` row: open_workspace_id=NONE, is_prunable=false
```
[verified-by-execution]

Docs explicitly distinguish `workspace close` (herdr state only) from `worktree remove` (delete the checkout), and the workspace/tab close paths in source mutate only tabs and workspaces — only deferred worktree removal invokes git. [read-from-source: herdr.dev CLI reference, worktrees section; `src/workspace.rs:close_tab`/`close_pane`; `src/app/api/worktrees/deferred.rs:handle_api_worktree_remove_finished`]

The decisive symbol is `handle_tab_close`: it computes `closes_workspace = ws.tabs.len() <= 1`, calls `state.close_selected_workspace()`, and emits `TabClosed`/`WorkspaceClosed`. **There is no worktree-removal call anywhere in that path.** [read-from-source: `src/app/api/tabs.rs:handle_tab_close`]

This **exactly explains** the observed orphans `work/729-change-recipe-proposal` and `work/743-proposal-review-authorization`: their workspaces are gone and their checkouts remain, because nothing in the close path was ever going to remove them. [inference from execution plus docs and source]

## 6. `--base` semantics

`worktree create` accepts `--base <REF>`; source defaults an omitted base to the literal `HEAD`. [read-from-source: `src/cli/worktree.rs:worktree_create`; `src/app/api/worktrees/deferred.rs:start_api_worktree_create`]

Branch handling forks on whether the branch already exists locally (`git show-ref --verify refs/heads/<branch>`):

- **Exists** → `git worktree add <path> <branch>`, and **`--base` is ignored**.
- **Does not exist** → `git worktree add -b <branch> <path> <base>`.

[read-from-source: `src/worktree.rs:run_worktree_add_command`/`build_worktree_add_existing_branch_command`/`build_worktree_add_new_branch_command`]

Consequences:

- A remote ref such as `origin/main` works **only if git can already resolve it locally**. **No fetch is performed.** [read-from-source + inference from the exact command construction]
- An absent or unfetched ref is handed to git as the start-point and fails with git's error; herdr reports `worktree_create_failed` and does **not** fall back to fetching or to current HEAD. [read-from-source: `src/worktree.rs:run_worktree_command`; `deferred.rs:handle_api_worktree_add_finished`]
- **Omitting `--base` means the current HEAD of the source checkout**, not `origin/main`. [read-from-source] This is the mechanism behind the 72-commits-stale worktree that produced 4,922 green tests against a dead baseline.

## Cleanup — verified, not asserted

```
$ test ! -e /tmp/wf-scratch-aVdb && echo GONE
GONE
$ test ! -e /home/polyphonyrequiem/.herdr/worktrees/repo && echo GONE
GONE
$ herdr workspace list | jq -r '[.result.workspaces[].workspace_id]|join(" ")'
w1 w3 w6 w7 wG wQ wX wZ w0 w11 w12 w13 w14 w16 w17
$ git -C /home/polyphonyrequiem/repos/twig worktree list | wc -l
33
```
[verified-by-execution]

Every scratch workspace created here (`w18`, `w19`, `w1A`, `w1B`) is absent from the final `workspace list`. Twig's worktree count is unchanged at 33. No herdr or git removal command was run against any artifact this session did not create.

## Appendix — installed CLI surface

Read from the installed binary, not from docs. [verified-by-execution]

`herdr worktree` — `list`, `create`, `open`, `remove`.

| Verb | Flags |
|---|---|
| `worktree create` | `--workspace <ID>`, `--cwd <PATH>`, `--branch <NAME>`, `--base <REF>`, `--path <PATH>`, `--label <TEXT>`, `--focus`, `--no-focus` |
| `worktree open` | `--workspace <ID>`, `--cwd <PATH>`, `--path <PATH>`, `--branch <NAME>`, `--label <TEXT>`, `--focus`, `--no-focus` |
| `worktree remove` | `--workspace <ID>`, `--force` |
| `worktree list` | `--workspace <ID>`, `--cwd <PATH>` |
| `tab create` | `--workspace <WORKSPACE_ID>`, `--cwd <PATH>`, `--label <TEXT>`, `--env <KEY=VALUE>`, `--focus`, `--no-focus` |
| `tab close` | positional `<tab_id>` |
| `workspace close` | positional `<workspace_id>` |

`herdr workspace` subcommands: `list`, `create`, `get`, `focus`, `rename`, `report-metadata`, `close`.

Note for ticket 0006: **`worktree remove` takes `--workspace`, not a path.** A reaping rule cannot address an orphaned checkout through this verb at all, because an orphan by definition has no workspace. Reaping an orphan therefore requires plain `git worktree remove`, entirely outside herdr's knowledge.
