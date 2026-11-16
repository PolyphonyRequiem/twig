---
command: process
group: process
summary: Show process configuration - list types (no args) or type details (with type name).
stability: stable
mutates: none
---

# `twig process`

Show process configuration: list types (no args) or type details (with type name).

## Behavior

Describes the connected schema or one discovered type. Types, states, fields, transitions and gaps come from the provider. An unavailable or incomplete schema is not an empty schema or permission to mutate.

Syntax, required arguments and defaults come from `twig process --help`.
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
twig process "<discovered-type>" -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
