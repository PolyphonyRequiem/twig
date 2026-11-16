---
command: connection writes
group: system
summary: Inspect native remote-write intents, immutable acknowledgments and uncertainty blockers for the attached checkout. Read-only; never replay, expire or discard an unknown write.
stability: stable
mutates: none
---

# `twig connection writes`

Inspect native remote-write intents, immutable acknowledgments and uncertainty blockers for the attached checkout. Read-only; never replay, expire or discard an unknown write.

## Behavior

Inspects bounded, identity/hash-checked native transport evidence without work HTTP. The evidence is correlated to original store/connection/digest/ordinal/attempt; only the Core proposal journal is execution authority. No settling, replay, fence clearing or evidence deletion occurs.

Syntax, required arguments and defaults come from `twig connection writes --help`.
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
twig connection writes --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
