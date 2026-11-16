---
command: bench delete
group: bench
summary: Delete a Bench. One holding pins reports what it holds and deletes nothing.
stability: stable
mutates: local
---

# `twig bench delete`

Delete a Bench. One holding pins reports what it holds and deletes nothing.

## Behavior

Deletes a local shared Bench definition, not upstream items. Nonempty contents require explicit confirmation and retained identity/content checks. This is not a force-delete compatibility route.

Syntax, required arguments and defaults come from `twig bench delete --help`.
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
twig bench delete --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
