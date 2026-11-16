---
command: workspace area sync
group: workspace
summary: Refresh native planning metadata only; does not replace or populate shared Bench rules.
stability: stable
mutates: local
---

# `twig workspace area sync`

Refresh native planning metadata only; does not replace or populate shared Bench rules.

## Behavior

Refresh native planning metadata only; does not replace or populate shared Bench rules. Uses the retained Core/native path and refuses unavailable provider/schema capabilities. It does not restore legacy Domain orchestration, implicit provider fallback or captured UI transport. These are retained contextual rule/view operations, not the removed bare area aliases or a full-screen sprint application. Use discovered planning paths, never assumed process names.

Syntax, required arguments and defaults come from `twig workspace area sync --help`.
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
twig workspace area sync --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
