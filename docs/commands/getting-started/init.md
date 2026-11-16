---
command: init
group: getting-started
summary: Initialize one fresh native Core workspace at .twig/core.db. No legacy cache, migration or reset.
stability: stable
mutates: local
---

# `twig init`

Initialize one fresh native Core workspace at .twig/core.db. No legacy cache, migration or reset.

## Behavior

Creates only a fresh Core store after provider/binding and filesystem preflight. Existing or partial state refuses; there is no migration, adoption, force or reinitialization route. ADO needs an enrolled binding, GitHub an explicit named gh account, and the Twig provider its durable native authority.

Syntax, required arguments and defaults come from `twig init --help`.
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
twig init --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
