---
command: update
group: work-items
summary: Update a field on the active work item.
stability: stable
mutates: provider
---

# `twig update`

Update a field on the active work item.

## Behavior

Resolves the field and typed value using the bound schema. Published targets stage, preview and save the draft through Core immediately; local seeds retain a local edit without publication. Structured calls require --id. Preserve rich-text format and inspect pending intent before changing an already-dirty target.

Syntax, required arguments and defaults come from `twig update --help`.
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
twig update System.Title "New title" --id 1234 -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
