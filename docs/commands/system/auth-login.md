---
command: auth login
group: system
summary: Enroll an explicit identity. ADO uses loopback PKCE by default; --provider github uses Twig's GitHub App device flow without a shipped secret.
stability: stable
mutates: local
---

# `twig auth login`

Enroll an explicit identity. ADO uses loopback PKCE by default; --provider github uses Twig's GitHub App device flow without a shipped secret.

## Behavior

Explicitly enrolls the supported named identity. Authentication/setup is separate authority from reading work; never expose tokens or private credential state in arguments, notes or logs.

Syntax, required arguments and defaults come from `twig auth login --help`.
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
twig auth login --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
