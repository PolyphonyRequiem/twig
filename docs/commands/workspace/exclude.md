---
command: workspace exclude
group: workspace
summary: Exclude a work item from workspace view.
stability: stable
mutates: local
---

# `twig workspace exclude`

Exclude a work item from workspace view.

## Behavior

Changes a local workspace-view exclusion rule, not shared Bench membership or upstream work. Excluded local drafts remain explicit rather than disappearing silently.

Syntax, required arguments and defaults come from `twig workspace exclude --help`.
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
twig workspace exclude --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
