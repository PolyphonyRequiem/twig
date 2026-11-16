---
command: edit
group: work-items
summary: Edit work item fields in an external editor.
stability: stable
mutates: provider
---

# `twig edit`

Edit work item fields in an external editor.

## Behavior

Uses the supported native editor path for the selected item, preserving provider/schema, revision, draft and digest checks. It is not a captured browser envelope or a second publisher. Unsupported provider/forms refuse.

Syntax, required arguments and defaults come from `twig edit --help`.
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
twig edit --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
