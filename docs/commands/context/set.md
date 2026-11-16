---
command: set
group: context
summary: Change the local human active-item pointer without claiming work.
stability: stable
mutates: local
---

# `twig set`

```sh
twig set 12345
twig set "authentication"
```

## Behavior

Selects one exact numeric target or an unambiguous cached title/pattern match.
Human selection can prompt among multiple matches. Missing native observations
can be acquired before selection. No match or cancelled/ambiguous selection
preserves the prior pointer.

The pointer is local context, not assignment, ownership, or a work claim.
Structured `set` and GitHub interactive selection are refused; scripts use
explicit read targets and `--id` for mutations.

## Effects

Writes local context/history and can acquire missing provider data.
Does not publish item edits or switch another worktree's context.

## Failure modes

Absent/ambiguous aliases, unavailable items, changed connection/store, or
unsupported machine/provider invocation fail without guessing a target.
