---
command: bench switch
group: bench
summary: Stand on another Bench. Naming one that does not exist fails; nothing is created.
stability: stable
mutates: local
---

# `twig bench switch`

Stand on another Bench. Naming one that does not exist fails; nothing is created.

## Behavior

Selects an existing Bench in the admitted local context. An unknown name fails without creating a default or changing another worktree/session selection.

Syntax, required arguments and defaults come from `twig bench switch --help`.
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
twig bench switch "Release work"
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
