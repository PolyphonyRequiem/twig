---
command: auth pat
group: system
summary: Enroll or renew a Personal Access Token identity. The PAT is read from a non-echoing Spectre prompt on a TTY, or from stdin when --stdin is set or stdin is redirected; it is never accepted as an argument. The service performs authoritative read-only principal attestation before touching any stored credential.
stability: stable
mutates: local
---

# `twig auth pat`

Enroll or renew a Personal Access Token identity. The PAT is read from a non-echoing Spectre prompt on a TTY, or from stdin when --stdin is set or stdin is redirected; it is never accepted as an argument. The service performs authoritative read-only principal attestation before touching any stored credential.

## Behavior

Explicitly enrolls a named PAT identity using the supported secret-input boundary. Never place credential values in tracker content, logs or examples.

Syntax, required arguments and defaults come from `twig auth pat --help`.
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
twig auth pat --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
