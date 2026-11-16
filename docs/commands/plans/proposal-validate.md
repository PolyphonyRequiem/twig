---
command: proposal validate
group: plans
summary: Validate a proposal's canonical file format offline. Preview separately checks application evidence; no mutation.
stability: stable
mutates: none
---

# `twig proposal validate`

Validate a proposal's canonical file format offline. Preview separately checks application evidence; no mutation.

## Behavior

Reads only the current Core version-3 document and reports canonical validation/digest information. Old v1/v2 envelopes and PlanCommand models are unsupported. Validation does not publish or grant mutation authority.

Syntax, required arguments and defaults come from `twig proposal validate --help`.
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
twig proposal validate --file .twig\proposals\change.json -o json
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
