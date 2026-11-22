# Connections, Benches, and context

## The model

| Concept | Meaning |
| --- | --- |
| Connection | A particular provider source and admitted identity, with immutable source coordinates checked at runtime |
| Workspace | A Git worktree's initialized Core client store and local context; also the name of the static `workspace` view command |
| Bench | A named, durable, **user-wide** selection of work, independent of any one repository |
| Active item | A local focus pointer, not assignment, ownership, or a work claim |

One Bench can retain selectors from multiple connections. The CLI and daemon
share Bench definitions in the native user store; viewers must not maintain a
second authoritative copy.

## Create, select, and pin

```sh
twig bench create MyWork
twig bench list
twig bench switch MyWork
twig workspace track 12345
twig workspace
```

Creating does not select a Bench. No automatic repository-derived default is
created. Metadata create/list/rename/delete works without an initialized
workspace; selecting and contextual pin operations require an admitted Core
workspace. Rename preserves the identity and rules.

A pin selects one item; a subtree selector and a typed query express different
intent. Area and iteration predicates remain rules, not frozen lists of today's
results. `workspace exclude` changes a local view rule without removing a shared
Bench selector or discarding a draft.

## Reads, pulls, and planning

Workspace output is a cached projection. A connection-scoped CLI view is not a
complete cross-connection refresh. `workspace sync` pulls permitted current Bench
selector members without flushing edits; ordinary `sync` can save pending edits
before pulling. Use `sync --pull-only` when publication is not intended.

The retained ADO planning surface includes `bench rules`, `bench rule add/remove`,
`bench planning show/refresh`, `bench sources`, `bench browse`, and
`workspace area` / `workspace sprint` management. Use each route's `--help` for
typed inputs and revision preconditions. These are native planning/static read
routes, not restoration of the retired live Bench browser or captured UI envelopes.
They are not promises of GitHub parity.

## Local context versus shared intent

`twig set <id>` selects an active item for human CLI convenience. It does not
assign, claim, or exclusively lock the work. A worktree's Bench selection and
active-item history do not switch another worktree's selection.

Machine calls must not rely on that pointer. Structured `show`/targeted `tree`
need explicit targets, and single-item mutations require `--id`. Structured
workspace output excludes active-item expansion while preserving explicit Bench
scope and drafts. Structured `set`, navigation, and seed publication are refused.

The daemon uses isolated view profiles and explicit source admission. Repository
bindings can constrain available sources without rewriting shared Bench intent.
On context entry for a verified repository, the daemon tries the owner's last
valid explicit choice for that repository (shared with linked worktrees), then
its saved Default Bench. Without a verified repository, only the session's
choice is considered. A concurrent explicit choice takes precedence over
startup selection. Changing Benches keeps the exact viewing source if it still
belongs to the new Bench, and keeps the Add items source if it is still admitted
in the current scope. A successful workspace without a
selection means no candidate exists in the catalogue. Read or access failures
aren't no-selection results, and a refresh does not rerun startup selection.
Remembering a choice does not change the default or share source admission,
permissions, drafts, or CLI selection.
Registered connection account labels use only the exact bound identity's local
display metadata, never another signed-in account or credential material.
Missing sources are reported as degraded/unavailable, not empty membership.
An attached source with zero members remains a dependency.
See [daemon operation](../daemon.md).
