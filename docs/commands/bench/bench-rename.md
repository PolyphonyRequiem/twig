---
command: bench rename
group: bench
summary: Rename a user-wide Bench without changing its pins or context selections.
stability: stable
mutates: local
---

# `twig bench rename`

Rename a user-wide Bench without changing its pins or context selections.

## Behavior

Renames the shared definition while preserving its stable identity, pins and contextual selections. Captured identity/contents checks refuse concurrent replacement instead of silently adopting it.

Syntax, required arguments and defaults come from `twig bench rename --help`.
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
twig bench rename "Release work" "Current work"
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
