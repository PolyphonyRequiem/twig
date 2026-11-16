---
command: link reparent
group: links
summary: Parent replacement is unsupported; refuses before HTTP without removing any link.
stability: stable
mutates: provider
---

# `twig link reparent`

Parent replacement is unsupported; refuses before HTTP without removing any link.

## Behavior

Parent replacement is unsupported; refuses before HTTP without removing any link. Uses the retained Core/native path and refuses unavailable provider/schema capabilities. It does not restore legacy Domain orchestration, implicit provider fallback or captured UI transport. Preserve exact source/target identities, relation direction and discovered provider semantics. Local-only Association edges never become remote relationships; unsupported provider effects refuse rather than falling back locally.

Syntax, required arguments and defaults come from `twig link reparent --help`.
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
twig link reparent --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
