---
command: proposal preview
group: plans
summary: Preview a proposal with an optional digest guard. No ADO mutation.
stability: stable
mutates: local
---

# `twig proposal preview`

Preview a proposal with an optional digest guard. No ADO mutation.

## Behavior

Reads an explicit current v3 file, checks exact connection/schema/store/policy evidence and retains native review/journal state. Reports digest, canApply and Core review data without a provider mutation. There are no full/interactive/width/color/include-rendering options or captured UI envelopes.

Syntax, required arguments and defaults come from `twig proposal preview --help`.
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
twig proposal preview --file .twig\proposals\change.json -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
