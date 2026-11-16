---
command: batch
group: work-items
summary: Apply explicit typed field/state/comment intent through one ordered Core proposal.
stability: stable
mutates: ado
---

# `twig batch`

```sh
twig batch --id 12345 --set "System.Title=New title" -o json
twig batch --id 12345 --state <actual-state> --set "<required-field>=<allowed-value>"
```

## Behavior

Requires explicit positive `--id` or `--ids` and at least one effect.
`--set` is repeatable. Field references and values are validated against the
actual target type. `--state` names an exact unambiguous discovered state;
batch does not prefix-resolve or chain intermediate transitions.

State and coupled required fields ride in the same operation. Core prepares
and applies one ordered proposal under current native/schema/policy evidence.
ADO atomic batches and GitHub observed-non-atomic effects remain distinct.
The command never auto-accepts remote conflicts or falls back to a legacy writer.

## Effects

Can publish immediately. Multiple targets are not an all-or-nothing distributed
transaction. Inspect original operation receipts after any failure.
An optional note follows the qualified provider operation contract, not a
separate implicit legacy pending-note flush.

## Failure modes

Invalid/missing scope or typed fields/state is a usage error. Policy/rule,
revision/admission, denied, partial or unresolved effects fail.
Never infer success from a partial response or retry a verified prefix.
