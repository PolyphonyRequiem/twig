---
command: workspace
group: workspace
summary: Read cached Core Bench membership and all protected drafts as a static table. Acquires provider schema metadata.
stability: stable
mutates: local
---

# `twig workspace`

Read cached Core Bench membership and all protected drafts as a static table. Acquires provider schema metadata.

## Behavior

Renders a static cached projection of the selected Bench/connection, not an interactive browser. Structured output excludes the active-item pointer and its incidental relationships, retaining explicit Bench scope and local drafts.

Syntax, required arguments and defaults come from `twig workspace --help`.
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
twig workspace -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
