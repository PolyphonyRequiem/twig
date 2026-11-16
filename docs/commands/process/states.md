---
command: states
group: process
summary: Human-only alias for the active work item's states; structured callers must use process &lt;type&gt;.
stability: stable
mutates: none
---

# `twig states`

Human-only alias for the active work item's states; structured callers must use process &lt;type&gt;.

## Behavior

Displays discovered state choices for human use. Structured states output is refused; use process with an explicitly discovered type for machine reads.

Syntax, required arguments and defaults come from `twig states --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `none`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig states
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
