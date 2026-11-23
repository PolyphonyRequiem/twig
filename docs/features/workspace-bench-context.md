# Connections, Benches, and context

## The model

| Concept | Meaning |
| --- | --- |
| Connection | A particular provider source and admitted identity, with immutable source coordinates checked at runtime |
| Workspace | A Git worktree's initialized Core client store; `workspace --bench <name-or-id>` reads one Bench in that store |
| Bench | A named, durable, **user-wide** selection of work, independent of any one repository |

One Bench can retain selectors from multiple connections. The CLI and server
share Bench definitions in the native user store; viewers must not maintain a
second authoritative copy.

## Create and inspect a Bench

```sh
twig bench create MyWork
twig bench list
twig workspace --bench MyWork
```

Creating does not select a Bench. No automatic repository-derived default is
created. Metadata create/list/rename/delete works without an initialized
workspace; reading item membership requires an admitted Core workspace.
Rename preserves the identity and rules.

A pin selects one item; a subtree selector and a typed query express different
intent. Area and iteration predicates remain rules, not frozen lists of today's
results. `workspace exclude` changes a local view rule without removing a shared
Bench selector or discarding a draft.

## Reads, pulls, and planning

Workspace output is a cached projection. A connection-scoped CLI view is not a
complete cross-connection refresh. `twig workspace --bench MyWork --refresh`
pulls permitted selector members for that Bench without flushing edits.

The retained ADO planning surface includes `bench rules`, `bench rule add/remove`,
`bench planning show/refresh`, `bench sources` and `bench browse`.
Use each route's `--help` for
typed inputs and revision preconditions. These are native planning/static read
routes, not restoration of the retired live Bench browser or captured UI envelopes.
They are not promises of GitHub parity.

## Local context versus shared intent

The CLI has no current item or current Bench. `twig show 12345` addresses an
item in this invocation; it cannot set a target for the next one. A named
Bench read includes its permitted selector scope and protected local drafts,
not extra relationships around an item in a viewer.

The server uses isolated view profiles and explicit source admission. In a
repository view, an attached Bench can use the repository's declared source
without a second attachment to the Bench. For example, if the repository
declares an ADO project, a new empty Bench can show that source with zero
members. Its pins and queries still determine membership. Opening the same
Bench from another repository does not carry the first repository's source
with it, change the Bench revision, or grant permission to publish.

Declaring a target is not the same as signing in. Twig must resolve it to
the exact local binding and workspace; it does not guess between accounts
or create a registration on read. An unavailable or excluded source needs
attention in repository setup, not a duplicate Bench attachment. Explicit
Bench dependencies remain visible even when unavailable in a repository.
Linked worktrees share bindings through their common Git directory. Twig uses
the main checkout as the repository context when there is one. When the common
directory is bare, Twig uses the requesting linked checkout instead; the bare
directory itself is not a checkout.

An existing server session retains its own saved Bench choice. A new session
uses the last explicit Bench selection for that verified repository, including
linked worktrees and after a server restart; a deleted remembered Bench falls
back to the configured repository default. Remembering a choice does not change
that default or share source admission, permissions, drafts, or CLI selection.
Registered connection account labels use only the exact bound identity's local
display metadata, never another signed-in account or credential material.
Missing sources are reported as degraded/unavailable, not empty membership.
An attached source with zero members remains a dependency.
See [server operation](../server.md).
