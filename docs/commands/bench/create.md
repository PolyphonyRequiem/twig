---
command: bench create
group: bench
summary: Create a Bench with a name you will recognise later.
stability: stable
mutates: local
---

# `twig bench create`

Create a Bench with a name you will recognise later.

## Behavior

Creates a shared user-wide Bench definition without selecting it or creating provider work. Metadata management can run without an initialized worktree. There is no automatic repository-derived default Bench.

Syntax, required arguments and defaults come from `twig bench create --help`.
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
twig bench create "Release work"
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
