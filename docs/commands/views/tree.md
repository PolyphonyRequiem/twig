---
command: tree
group: views
summary: Display a targeted static Core work-item hierarchy; full workspace layout is unsupported.
stability: stable
mutates: local
---

# `twig tree`

Display a targeted static Core work-item hierarchy; full workspace layout is unsupported.

## Behavior

Renders a bounded static hierarchy for an explicit/active target using retained Core semantics. Structured reads need a positional ID or --id. There is no live/full-backlog tree application; unsupported GitHub shapes refuse.

Syntax, required arguments and defaults come from `twig tree --help`.
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
twig tree 1234
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
