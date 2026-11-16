---
command: sync
group: getting-started
summary: Save retained remote drafts through the proposal journal, then pull the selected Bench.
stability: stable
mutates: provider
---

# `twig sync`

Save retained remote drafts through the proposal journal, then pull the selected Bench.

## Behavior

Uses the retained qualified ADO sync path. --pull-only refreshes without publishing local edits. Normal sync may save intent before pulling; inspect pending work first. This is not a legacy repository fallback or a GitHub parity promise.

Syntax, required arguments and defaults come from `twig sync --help`.
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
twig sync --pull-only
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
