---
command: bench rules
group: bench
summary: List the exact shared Bench pins/rules and captured revision; no provider reads.
stability: stable
mutates: none
---

# `twig bench rules`

List the exact shared Bench pins/rules and captured revision; no provider reads.

## Behavior

Lists exact stored shared pins/rules and the captured Bench revision without provider reads. Rule conditions are AND; independent pins/rules are OR.

Syntax, required arguments and defaults come from `twig bench rules --help`.
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
twig bench rules "Release work" -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
