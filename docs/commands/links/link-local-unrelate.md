---
command: link local unrelate
group: links
summary: Tombstone an existing LOCAL Related association through its exact local CAS. Native reads may occur; no native mutation.
stability: stable
mutates: local
---

# `twig link local unrelate`

Tombstone an existing LOCAL Related association through its exact local CAS. Native reads may occur; no native mutation.

## Behavior

Tombstone an existing LOCAL Related association through its exact local CAS. Native reads may occur; no native mutation. Uses the retained Core/native path and refuses unavailable provider/schema capabilities. It does not restore legacy Domain orchestration, implicit provider fallback or captured UI transport. Preserve exact source/target identities, relation direction and discovered provider semantics. Local-only Association edges never become remote relationships; unsupported provider effects refuse rather than falling back locally.

Syntax, required arguments and defaults come from `twig link local unrelate --help`.
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
twig link local unrelate --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
