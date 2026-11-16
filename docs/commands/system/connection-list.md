---
command: connection list
group: system
summary: List bindings for an endpoint (org/project). Falls back to the workspace's checked-in coordinates when both --org and --project are omitted.
stability: stable
mutates: none
---

# `twig connection list`

List bindings for an endpoint (org/project). Falls back to the workspace's checked-in coordinates when both --org and --project are omitted.

## Behavior

Lists recorded native bindings without asserting fresh authenticated access or changing a default/context.

Syntax, required arguments and defaults come from `twig connection list --help`.
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
twig connection list
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
