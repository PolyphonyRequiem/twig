---
command: workspace untrack
group: workspace
summary: Remove explicit pins, optionally only single or tree; inherited membership remains.
stability: stable
mutates: local
---

# `twig workspace untrack`

Remove explicit pins, optionally only single or tree; inherited membership remains.

## Behavior

Remove explicit pins, optionally only single or tree; inherited membership remains. Uses the retained Core/native path and refuses unavailable provider/schema capabilities. It does not restore legacy Domain orchestration, implicit provider fallback or captured UI transport.

Syntax, required arguments and defaults come from `twig workspace untrack --help`.
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
twig workspace untrack --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
