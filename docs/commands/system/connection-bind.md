---
command: connection bind
group: system
summary: Bind an endpoint (org/project) to a registered identity. Use --default to request this as the initial default; the service refuses to switch an existing different default.
stability: stable
mutates: local
---

# `twig connection bind`

Bind an endpoint (org/project) to a registered identity. Use --default to request this as the initial default; the service refuses to switch an existing different default.

## Behavior

Creates/reuses an explicitly named binding using recorded enrollment and exact provider coordinates. It does not hot-switch an existing admitted workspace or infer an ADO target from GitHub.

Syntax, required arguments and defaults come from `twig connection bind --help`.
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
twig connection bind --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
