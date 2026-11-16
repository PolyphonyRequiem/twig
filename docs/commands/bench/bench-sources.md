---
command: bench sources
group: bench
summary: Discover native saved-query hierarchy and exact-team backlogs; does not execute them or change selection.
stability: stable
mutates: local
---

# `twig bench sources`

Discover native saved-query hierarchy and exact-team backlogs; does not execute them or change selection.

## Behavior

Discovers saved-query hierarchy and exact-team backlogs without executing membership or changing selection.

Syntax, required arguments and defaults come from `twig bench sources --help`.
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
twig bench sources -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
