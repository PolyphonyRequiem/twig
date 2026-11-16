---
command: show-batch
group: context
summary: Display multiple work items by ID (cache-only). Missing IDs are disclosed and exit nonzero.
stability: stable
mutates: none
---

# `twig show-batch`

Display multiple work items by ID (cache-only). Missing IDs are disclosed and exit nonzero.

## Behavior

Reads explicitly selected targets without consulting or changing active context. Preserve per-item unavailable/error results and the exact supported structured projection; do not treat missing data as an empty successful item.

Syntax, required arguments and defaults come from `twig show-batch --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `none`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig show-batch --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
