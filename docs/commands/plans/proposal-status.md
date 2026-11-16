---
command: proposal status
group: plans
summary: Inspect journal states and receipts for an exact proposal digest without applying or settling it; exit 1 when absent.
stability: stable
mutates: none
---

# `twig proposal status`

Inspect journal states and receipts for an exact proposal digest without applying or settling it; exit 1 when absent.

## Behavior

Inspects the current v3 file and its Core journal, preserving every operation outcome and original attempt identity. Transport evidence from connection writes is correlated evidence, not a replacement execution journal.

Syntax, required arguments and defaults come from `twig proposal status --help`.
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
twig proposal status --file .twig\proposals\change.json -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
