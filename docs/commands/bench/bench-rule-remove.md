---
command: bench rule remove
group: bench
summary: Remove one captured shared pin/rule without deleting any upstream item or local draft.
stability: stable
mutates: local
---

# `twig bench rule remove`

Remove one captured shared pin/rule without deleting any upstream item or local draft.

## Behavior

Removes one one-based rule index from a fresh rules listing, requiring that captured Bench revision. It never removes provider items or local drafts.

Syntax, required arguments and defaults come from `twig bench rule remove --help`.
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
twig bench rule remove --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
