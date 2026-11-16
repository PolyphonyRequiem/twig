---
command: sync
group: getting-started
summary: Save eligible existing-item drafts, then pull the selected Bench; pull-only never saves.
stability: stable
mutates: ado
---

# `twig sync`

```sh
twig sync --pull-only
twig workspace sync
twig sync
```

## Behavior

Requires an explicitly selected existing Core Bench. Ordinary `sync` first saves
eligible existing-item drafts through a digest-bound proposal and journal, then
pulls permitted current Bench selectors/relationship context. It does not publish
seeds or replace shared rules with today's members.

`--pull-only` skips saving. `workspace sync` is also pull-only.
Human pulls may include qualified active-item context; machine pulls exclude
that implicit expansion while retaining explicit Bench scope and drafts.
GitHub sync conveniences and old browser-origin envelopes are not qualified.

## Effects

Ordinary sync can publish **all eligible existing-item drafts** in its save scope.
Both modes can perform provider reads and cache writes. No mode is a full
unbounded backlog refresh.

## Failure modes

Missing Bench/admission or protected unresolved intent refuses.
A save can succeed before a later pull fails; inspect the reported original
digest and journal rather than retrying the whole operation blindly.
Cancellation/failure retains acknowledged effects and drafts without replay.
