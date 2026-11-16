---
command: auth identities
group: system
summary: List every registered identity (AAD and PAT) with safe principal metadata. Credential blobs are never surfaced; AAD rows show tenant/objectId/authorityHost, PAT rows show adoPrincipalId/adoAuthority.
stability: stable
mutates: none
---

# `twig auth identities`

List every registered identity (AAD and PAT) with safe principal metadata. Credential blobs are never surfaced; AAD rows show tenant/objectId/authorityHost, PAT rows show adoPrincipalId/adoAuthority.

## Behavior

Lists recorded named identity metadata. A recorded identity is not proof of fresh provider access or permission to publish.

Syntax, required arguments and defaults come from `twig auth identities --help`.
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
twig auth identities
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
