---
command: patch
group: work-items
summary: Atomically patch multiple fields on a work item via JSON input.
stability: stable
mutates: provider
---

# `twig patch`

Atomically patch multiple fields on a work item via JSON input.

## Behavior

Applies the explicitly supplied field changes through the current Core/provider path. Use the installed declaration for the JSON shape. Do not copy legacy raw REST transport or pending-writer assumptions.

Syntax, required arguments and defaults come from `twig patch --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `provider`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig patch --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
