---
command: bench browse
group: bench
summary: Explicitly acquire saved-query/backlog membership and item bodies into the native cache; never pins, selects or publishes.
stability: stable
mutates: local
---

# `twig bench browse`

Explicitly acquire saved-query/backlog membership and item bodies into the native cache; never pins, selects or publishes.

## Behavior

Acquires explicitly chosen saved-query/backlog membership and item bodies into the cache. It never pins, selects or publishes; this is not the retired full-screen Bench browser.

Syntax, required arguments and defaults come from `twig bench browse --help`.
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
twig bench browse --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
