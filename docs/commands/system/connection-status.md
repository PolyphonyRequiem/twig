---
command: connection status
group: system
summary: Show the identity + binding the current attached worktree resolves to. Fails with setup guidance when there is no attachment or the binding is ambiguous; no fallback.
stability: stable
mutates: none
---

# `twig connection status`

Show the identity + binding the current attached worktree resolves to. Fails with setup guidance when there is no attachment or the binding is ambiguous; no fallback.

## Behavior

Describes the recorded connection, identity and immutable target selection. It is not fresh authenticated evidence or a provider write-capability probe.

Syntax, required arguments and defaults come from `twig connection status --help`.
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
twig connection status
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
