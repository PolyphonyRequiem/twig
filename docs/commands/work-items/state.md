---
command: state
group: work-items
summary: Change the state of the active work item by name.
stability: stable
mutates: provider
---

# `twig state`

Change the state of the active work item by name.

## Behavior

Resolves the requested state through the bound schema and applies through current Core semantics, never the old direct-PATCH or auto-chained transition workflow. A transition cannot invent missing close-gate fields. Use one explicit proposal batch carrying the transition and all required fields when they must change together.

Syntax, required arguments and defaults come from `twig state --help`.
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
twig state "<discovered-state>" --id 1234 -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
