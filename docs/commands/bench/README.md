# `twig bench`

A **Bench** is a named, durable, saved backlog. You name an arrangement of work
once and return to it later; several Benches can exist side by side, and the one
you are currently standing on decides what your workspace view shows. Everything
standing on a Bench sees the same Bench — there are no private pins.

A Bench holds **selectors** (a pin is a selector that matches one item; a query
is a selector that matches a body of work). The store is durable: a Bench and its
selectors survive cache rebuilds, and the default Bench is always present.

The `bench` command group is the shared surface for managing that store — create
a Bench, list what exists, switch which one is current, or delete one you no
longer need. Every subcommand accepts `-o|--output human|json|minimal`; the
format is *declared* by the caller, never sniffed from the terminal, so a
command means the same thing in a pipe as at a prompt.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`bench create`](./create.md)|Create a Bench with a name you will recognise later.|local|
|[`bench list`](./list.md)|List the Benches that exist, marking the current one.|none|
|[`bench switch`](./switch.md)|Stand on another Bench.|local|
|[`bench delete`](./delete.md)|Delete a Bench — one holding pins refuses without `--confirm`.|local|

## Configure the current Bench

`bench configuration` reads explicit pins (including IDs not yet cached), saved
area and sprint filters, the existing automatic ownership scope, and a settings
digest. It does not fetch work items or edit workspace-global area/sprint settings.

```powershell
twig bench configuration -o json
twig bench configuration area add "Project\Team A"       # Under: this area and descendants
twig bench configuration area add "Project\Team A" --exact
twig bench configuration area remove "Project\Team A"
twig bench configuration sprint add @Current+1
twig bench configuration sprint add "Project\Release Sprint"
twig bench configuration sprint remove @Current
```

Area alternatives are ORed; sprint alternatives are ORed; the area, sprint and
saved assignee constraints intersect. No areas means no area restriction. No
sprints means no automatic membership, never a project-wide query. Explicit
single/tree pins, seeds and pending work remain additive regardless of filters.
`@Current`, `@Current-N`, and `@Current+N` resolve using the cached iteration calendar
and roll forward with time. Absolute iteration paths stay literal. Refresh the
calendar and scoped candidates with `twig workspace sync`, not workspace-global
area/sprint configuration commands. Existing `current-sprint` rules are preserved
until an edit and then saved as a versioned `bench-filter` selector. The default
Bench refreshes its canonical self principal without discarding authored filters.

All four add/remove commands accept `-o human|json|minimal` and optional captured
preconditions: `--expect-bench`, `--expect-binding`, `--expect-identity`, and
`--expect-settings`. The settings digest covers raw stored query selectors, not
in-memory self normalization. Native storage compares the current Bench and digest
inside the same transaction that replaces only its query selectors; pins are
untouched. A stale form fails instead of retargeting or merging its old settings.
Unknown or malformed query rules refuse instead of broadening the scope.

JSON reads and successful edits expose `version`, `settingsDigest`, `areas`
(`path`, `includeChildren`), `sprints` (`expression`), `automaticEnabled`,
`assigneeSummary`, and `pins` (`id`, `mode: single|tree`, `cached`, and optional
cached `title`, `type`, `state`), plus captured Bench/binding/identity IDs.
The same configuration object appears at `browser.configuration` only when
`workspace --view tree -o json --include-browser` is explicitly requested;
ordinary workspace JSON remains unchanged.

Any positive work item ID can be pinned with `workspace track` or
`workspace track-tree`, even on an empty Bench. Unknown IDs are uncached/unverified,
not invented work-item records. Single and subtree pins for the same ID coexist.
`workspace untrack --mode single` removes only the single pin, and
`workspace untrack --mode tree` removes only the subtree pin. Omitting `--mode`
still removes both explicit kinds for that ID. None of these operations hides
inherited subtree membership, query matches, seeds or pending work.
These pin commands also accept `--expect-settings` for captured browser forms;
typed removal reports the named kind and whether the transaction actually
deleted it, then consumers can refresh native truth.


## See also

- [`workspace`](../workspace/README.md) — the view that a Bench shapes.
