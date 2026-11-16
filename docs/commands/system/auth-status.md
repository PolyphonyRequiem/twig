---
command: auth status
group: system
summary: Inspect local authentication state without refreshing. Use --provider github for a selected GitHub App identity.
stability: stable
mutates: none
---

# `twig auth status`

Inspect local authentication state without refreshing. Use --provider github for a selected GitHub App identity.

## Behavior

Reports the recorded selected/pinned identity and target, not a fresh authentication check. Explicit GitHub App status describes only that named app credential and does not attest a pinned gh actor.

Syntax, required arguments and defaults come from `twig auth status --help`.
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
twig auth status
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
