# Twig command reference

Source-accurate reference for the current twig source build. Every accepted command has a dedicated page with its arguments, flags, side effects, examples, and failure modes. The `mutates` column is intended for both people and automation: `none` is read-only, `local` changes only local workspace state, and `ado` can write to Azure DevOps.

The reference uses the source command names. Deprecated aliases remain documented so existing scripts can resolve their behavior; prefer the canonical command named on each alias page.

| Group | Command | Summary | Stability | Mutates |
|---|---|---|---|---|
| [Getting Started](getting-started/README.md) | [`twig init`](getting-started/init.md) | Initialize a new Twig workspace at the current git-worktree root. | stable | `local` |
| [Getting Started](getting-started/README.md) | [`twig refresh`](getting-started/refresh.md) | Deprecated alias for `twig sync --pull-only` — refresh the local cache from Azure DevOps. | stable | `local` |
| [Getting Started](getting-started/README.md) | [`twig sync`](getting-started/sync.md) | Flush pending changes to Azure DevOps then refresh the local cache. | stable | `ado` |
| [Views](views/README.md) | [`twig sprint`](views/sprint.md) | Show sprint items grouped by assignee. | stable | `local` |
| [Views](views/README.md) | [`twig tree`](views/tree.md) | Hidden backward-compat alias that routes to show --tree, or workspace --tree with --all. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig area`](workspace/area-deprecated.md) | Deprecated alias for `workspace area`. Prints a deprecation hint on stderr. | stable | `none` |
| [Workspace](workspace/README.md) | [`twig area add`](workspace/area-add-deprecated.md) | Deprecated alias for `workspace area add`. Prints a deprecation hint on stderr. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig area list`](workspace/area-list-deprecated.md) | Deprecated alias for `workspace area list`. Prints a deprecation hint on stderr. | stable | `none` |
| [Workspace](workspace/README.md) | [`twig area remove`](workspace/area-remove-deprecated.md) | Deprecated alias for `workspace area remove`. Prints a deprecation hint on stderr. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig area sync`](workspace/area-sync-deprecated.md) | Deprecated alias for `workspace area sync`. Prints a deprecation hint on stderr. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace`](workspace/workspace.md) | Show the current workspace. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace area`](workspace/area.md) | Show the area-filtered workspace view. | stable | `none` |
| [Workspace](workspace/README.md) | [`twig workspace area add`](workspace/area-add.md) | Add an area path to workspace configuration. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace area list`](workspace/area-list.md) | List configured area paths with match semantics. | stable | `none` |
| [Workspace](workspace/README.md) | [`twig workspace area remove`](workspace/area-remove.md) | Remove an area path from workspace configuration. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace area sync`](workspace/area-sync.md) | Fetch team area paths from ADO and replace configuration. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace exclude`](workspace/exclude.md) | Exclude a work item from workspace view. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace exclusions`](workspace/exclusions.md) | List all excluded work items; also clears or removes exclusions. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace sprint add`](workspace/sprint-add.md) | Add a sprint iteration expression to workspace configuration. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace sprint list`](workspace/sprint-list.md) | List configured sprint iteration expressions. | stable | `none` |
| [Workspace](workspace/README.md) | [`twig workspace sprint remove`](workspace/sprint-remove.md) | Remove a sprint iteration expression from workspace configuration. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace track`](workspace/track.md) | Track a single work item by ID (pinned to workspace). | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace track-tree`](workspace/track-tree.md) | Track a work item and its subtree. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig workspace untrack`](workspace/untrack.md) | Remove a work item from tracking. | stable | `local` |
| [Workspace](workspace/README.md) | [`twig ws`](workspace/ws.md) | Short alias for `workspace` — show the current workspace. | stable | `local` |
| [Bench](bench/README.md) | [`twig bench create`](bench/create.md) | Create a Bench with a name you will recognise later. | stable | `local` |
| [Bench](bench/README.md) | [`twig bench delete`](bench/delete.md) | Delete a Bench — one holding pins refuses without --confirm. | stable | `local` |
| [Bench](bench/README.md) | [`twig bench list`](bench/list.md) | List the Benches that exist, marking the current one. | stable | `none` |
| [Bench](bench/README.md) | [`twig bench switch`](bench/switch.md) | Stand on another Bench. | stable | `local` |
| [Context](context/README.md) | [`twig history`](context/history.md) | Show the ADO revision history for a work item; read-only, never cached. | stable | `none` |
| [Context](context/README.md) | [`twig query`](context/query.md) | Search and filter work items via an ad-hoc WIQL query built from CLI flags. | stable | `local` |
| [Context](context/README.md) | [`twig set`](context/set.md) | Set the active work item by ID or title pattern. | stable | `local` |
| [Context](context/README.md) | [`twig show`](context/show.md) | Display a work item without changing context; cache-only by default. | stable | `none` |
| [Context](context/README.md) | [`twig show-batch`](context/show-batch.md) | Display multiple work items by ID from the local cache; missing IDs are silently skipped. | stable | `none` |
| [Context](context/README.md) | [`twig tree-set`](context/tree-set.md) | Render an arbitrary working set of work items as a forest of annotated trees. | stable | `none` |
| [Context](context/README.md) | [`twig web`](context/web.md) | Open the active or specified work item in Azure DevOps in the default browser. | stable | `none` |
| [Navigation](navigation/README.md) | [`twig back`](navigation/back.md) | Deprecated alias for `nav back`. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig down`](navigation/down.md) | Deprecated alias for `nav down`. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig fore`](navigation/fore.md) | Deprecated alias for `nav fore`. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav`](navigation/nav.md) | Launch the interactive tree navigator. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav back`](navigation/nav-back.md) | Move the navigation-history cursor one entry backward. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav down`](navigation/nav-down.md) | Set the active work item to one of the current item's children. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav fore`](navigation/nav-fore.md) | Move the navigation-history cursor one entry forward. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav history`](navigation/nav-history.md) | Display or pick from the navigation history stack. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav next`](navigation/nav-next.md) | Set the active work item to the next sibling. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav prev`](navigation/nav-prev.md) | Set the active work item to the previous sibling. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig nav up`](navigation/nav-up.md) | Set the active work item to the parent of the current one. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig next`](navigation/next.md) | Deprecated alias for `nav next`. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig prev`](navigation/prev.md) | Deprecated alias for `nav prev`. | stable | `local` |
| [Navigation](navigation/README.md) | [`twig up`](navigation/up.md) | Deprecated alias for `nav up`. | stable | `local` |
| [Process](process/README.md) | [`twig process`](process/process.md) | List work item types, or with a type argument show its states, fields, and transitions. | stable | `none` |
| [Process](process/README.md) | [`twig process <type>`](process/process-type.md) | Describe one dynamically discovered work-item type. | stable | `none` |
| [Process](process/README.md) | [`twig process description`](process/process-description.md) | Write a byte-stable structural description of the process, for diffing against another. | stable | `none` |
| [Process](process/README.md) | [`twig process layout`](process/process-layout.md) | Show the server-defined form layout — tabs, boxes, and ordered fields — for a work item type. | stable | `none` |
| [Process](process/README.md) | [`twig states`](process/states.md) | Hidden alias — list workflow states for the active work item's type. | stable | `none` |
| [Work Items](work-items/README.md) | [`twig batch`](work-items/batch.md) | State transition, field updates, and a note in a single atomic call. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig delete`](work-items/delete.md) | Permanently delete a work item from Azure DevOps. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig discard`](work-items/discard.md) | Drop pending changes for a single work item or all dirty items. | stable | `local` |
| [Work Items](work-items/README.md) | [`twig edit`](work-items/edit.md) | Edit work item fields interactively in an external editor. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig new`](work-items/new.md) | Create a new work item in ADO. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig note`](work-items/note.md) | Add a note (ADO comment) to the active work item. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig patch`](work-items/patch.md) | Atomically patch multiple fields on a work item via JSON input. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig state`](work-items/state.md) | Change the state of the active work item by name. | stable | `ado` |
| [Work Items](work-items/README.md) | [`twig update`](work-items/update.md) | Update a single field on the active work item. | stable | `ado` |
| [Links](links/README.md) | [`twig link artifact`](links/link-artifact.md) | Attach a hyperlink or vstfs artifact link to a work item. | stable | `ado` |
| [Links](links/README.md) | [`twig link parent`](links/link-parent.md) | Set the parent of the active (or targeted) work item. | stable | `ado` |
| [Links](links/README.md) | [`twig link predecessor`](links/link-predecessor.md) | Mark the active (or targeted) item as blocked by another item. | stable | `ado` |
| [Links](links/README.md) | [`twig link related`](links/link-related.md) | Add a symmetric Related link between two work items, optionally with a comment. | stable | `ado` |
| [Links](links/README.md) | [`twig link reparent`](links/link-reparent.md) | Remove the current parent and set a new one in a single operation. | stable | `ado` |
| [Links](links/README.md) | [`twig link successor`](links/link-successor.md) | Mark the active (or targeted) item as blocking another item. | stable | `ado` |
| [Links](links/README.md) | [`twig link unlink`](links/link-unlink.md) | Remove any non-hierarchy link (predecessor, successor, or related) from a work item. | stable | `ado` |
| [Links](links/README.md) | [`twig link unparent`](links/link-unparent.md) | Remove the parent link from the active (or targeted) work item. | stable | `ado` |
| [Links](links/README.md) | [`twig link unrelate`](links/link-unrelate.md) | Remove a symmetric Related link between two work items. | stable | `ado` |
| [Seeds](seeds/README.md) | [`twig seed`](seeds/seed.md) | Hidden backward-compat shortcut for `seed new`. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed chain`](seeds/seed-chain.md) | Create a chain of successor-linked seeds under a shared parent. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed discard`](seeds/seed-discard.md) | Delete a local seed and its descendants. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed edit`](seeds/seed-edit.md) | Edit a seed's fields in an external editor. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed link`](seeds/seed-link.md) | Create a virtual link between two items, at least one of which must be a seed. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed links`](seeds/seed-links.md) | List virtual links, optionally filtered by item ID. | stable | `none` |
| [Seeds](seeds/README.md) | [`twig seed new`](seeds/seed-new.md) | Create a new local seed work item. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed publish`](seeds/seed-publish.md) | Publish seeds to Azure DevOps. | stable | `ado` |
| [Seeds](seeds/README.md) | [`twig seed reconcile`](seeds/seed-reconcile.md) | Repair stale seed links and parent references after a partial publish. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed unlink`](seeds/seed-unlink.md) | Remove a virtual link between two items. | stable | `local` |
| [Seeds](seeds/README.md) | [`twig seed validate`](seeds/seed-validate.md) | Validate seeds against publish rules. | stable | `none` |
| [Seeds](seeds/README.md) | [`twig seed view`](seeds/seed-view.md) | Show the seed dashboard grouped by parent. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig pending`](plans/pending.md) | List raw staged pending changes in exact staging order. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig plan apply`](plans/plan-apply.md) | Deprecated alias for `proposal apply`. | stable | `ado` |
| [Plans and Proposals](plans/README.md) | [`twig plan preview`](plans/plan-preview.md) | Deprecated alias for `proposal preview`. | stable | `local` |
| [Plans and Proposals](plans/README.md) | [`twig plan seed`](plans/plan-seed.md) | Deprecated alias for `proposal seed`. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig plan status`](plans/plan-status.md) | Deprecated alias for `proposal status`. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig plan validate`](plans/plan-validate.md) | Deprecated alias for `proposal validate`. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig proposal apply`](plans/proposal-apply.md) | Apply a proposal after digest confirmation and identity authorization. | stable | `ado` |
| [Plans and Proposals](plans/README.md) | [`twig proposal preview`](plans/proposal-preview.md) | Preview a proposal — journal import, pending snapshot, digest, and canApply gate. | stable | `local` |
| [Plans and Proposals](plans/README.md) | [`twig proposal seed`](plans/proposal-seed.md) | Describe a staged seed's identity and fingerprint for proposal authoring. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig proposal status`](plans/proposal-status.md) | Show journal state for a proposal file, keyed on its digest. | stable | `none` |
| [Plans and Proposals](plans/README.md) | [`twig proposal validate`](plans/proposal-validate.md) | Validate a proposal v1 file without touching ADO. | stable | `none` |
| [Configuration](configuration/README.md) | [`twig config`](configuration/config.md) | Read or set a configuration value. | stable | `local` |
| [Configuration](configuration/README.md) | [`twig config status-fields`](configuration/config-status-fields.md) | Configure which fields appear in the status view. | stable | `local` |
| [Configuration](configuration/README.md) | [`twig help`](configuration/help.md) | Grouped help fast-path — canonical form is `twig --help`. | stable | `none` |
| [Configuration](configuration/README.md) | [`twig migrate-config`](configuration/migrate-config.md) | Split a legacy .twig/config into a committed twig.json and gitignored user prefs. | stable | `local` |
| [System](system/README.md) | [`twig auth clear`](system/auth-clear.md) | Wipe the refresh-token store and cached access token, and flush the in-process copy. | stable | `local` |
| [System](system/README.md) | [`twig auth login`](system/auth-login.md) | Sign in to Azure DevOps interactively and persist a refresh token under ~/.twig/. | stable | `local` |
| [System](system/README.md) | [`twig auth status`](system/auth-status.md) | Inspect the refresh-token store and cached ADO access token without ever printing the token. | stable | `none` |
| [System](system/README.md) | [`twig changelog`](system/changelog.md) | Display recent release notes from GitHub Releases without applying any update. | stable | `none` |
| [System](system/README.md) | [`twig upgrade`](system/upgrade.md) | Check GitHub Releases for a newer twig and apply the update, including companion binaries. | stable | `local` |
| [System](system/README.md) | [`twig version`](system/version.md) | Print the installed twig version. | stable | `none` |
| [Experimental](experimental/README.md) | [`twig mcp`](experimental/mcp.md) | Launch the Model Context Protocol server (requires twig-mcp companion binary). | experimental | `ado` |
| [Experimental](experimental/README.md) | [`twig ohmyposh init`](experimental/ohmyposh-init.md) | Emit an Oh My Posh shell hook and text-segment JSON for the current shell. | experimental | `none` |
| [Experimental](experimental/README.md) | [`twig tui`](experimental/tui.md) | Launch the full-screen interactive TUI (requires twig-tui companion binary). | experimental | `local` |

## Feature guides

- [Workspace, Bench, and Context](../features/workspace-bench-context.md)
- [Seeds and publishing](../features/seeds-and-publishing.md)
- [Plans and proposals](../features/proposals.md)
- [Authentication](../features/authentication.md)
- [Reference profile](../features/reference-profile.md)
- [Process description](../features/process-description.md)
