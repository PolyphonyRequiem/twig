---
command: ws
group: workspace
summary: Read the static cached Core Bench workspace (short alias); acquires provider schema metadata.
stability: stable
mutates: local
---

# `twig ws`

Read the static cached Core Bench workspace (short alias); acquires provider schema metadata.

## Behavior

Alias for workspace, with the same static cached projection and structured-scope behavior. It does not initialize, select or invent a default Bench.

Syntax, required arguments and defaults come from `twig ws --help`.
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
twig ws -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
