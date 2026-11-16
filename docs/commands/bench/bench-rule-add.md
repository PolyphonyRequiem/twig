---
command: bench rule add
group: bench
summary: Add one connection-qualified shared rule. Conditions inside it are AND; independent Bench pins/rules are OR.
stability: stable
mutates: local
---

# `twig bench rule add`

Add one connection-qualified shared rule. Conditions inside it are AND; independent Bench pins/rules are OR.

## Behavior

Adds one connection-qualified rule using discovered native planning metadata. Area, iteration, actor, saved-query and backlog references are intent, not frozen result lists. Optional expected revision refuses concurrent edits.

Syntax, required arguments and defaults come from `twig bench rule add --help`.
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
twig bench rule add --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
