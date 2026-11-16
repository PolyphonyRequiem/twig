---
command: batch
group: work-items
summary: Batch state transitions, field updates, and notes in a single call.
stability: stable
mutates: provider
---

# `twig batch`

Batch state transitions, field updates, and notes in a single call.

## Behavior

Combines supported scalar/state/note effects for explicit --id or --ids targets, then prepares and applies the ordered Core proposal immediately. Discover fields and state requirements first. Coupled state and gate-field changes share one operation per target; multiple targets are not a globally atomic transaction.

Syntax, required arguments and defaults come from `twig batch --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `provider`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig batch --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
