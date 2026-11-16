---
command: workspace exclusions
group: workspace
summary: List all excluded work items.
stability: stable
mutates: local
---

# `twig workspace exclusions`

List all excluded work items.

## Behavior

Inspects separately stored local workspace-view exclusions; these are not subtracting Bench selectors or provider deletions.

Syntax, required arguments and defaults come from `twig workspace exclusions --help`.
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
twig workspace exclusions --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
