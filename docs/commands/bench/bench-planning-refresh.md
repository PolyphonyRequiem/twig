---
command: bench planning refresh
group: bench
summary: Explicitly refresh native areas and the exact selected team's iteration calendar; no work items or Bench rules change.
stability: stable
mutates: local
---

# `twig bench planning refresh`

Explicitly refresh native areas and the exact selected team's iteration calendar; no work items or Bench rules change.

## Behavior

Explicitly refreshes native areas and the exact selected team iteration calendar, not work-item bodies, Bench rules or selection.

Syntax, required arguments and defaults come from `twig bench planning refresh --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `local`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig bench planning refresh
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
