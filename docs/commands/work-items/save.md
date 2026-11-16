---
command: save
group: work-items
summary: Save selected staged Core intent through its proposal journal; does not perform a sync pull.
stability: stable
mutates: provider
---

# `twig save`

Save selected staged Core intent through its proposal journal; does not perform a sync pull.

## Behavior

Publishes supported local intent through Core proposal/digest/journal semantics. Do not automatically save unrelated drafts to make another request succeed. Verify operation outcomes and refreshed provider readback.

Syntax, required arguments and defaults come from `twig save --help`.
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
twig save --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
