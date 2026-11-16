---
command: seed links
group: seeds
summary: List virtual links, optionally filtered by item ID.
stability: stable
mutates: none
---

# `twig seed links`

List virtual links, optionally filtered by item ID.

## Behavior

List virtual links, optionally filtered by item ID. Uses the retained Core/native path and refuses unavailable provider/schema capabilities. It does not restore legacy Domain orchestration, implicit provider fallback or captured UI transport. A seed is a local UUID-backed draft, not a negative published identity. Discover type/field/relationship rules from the bound schema; creation and verified publication are separate outcomes. After an uncertain attempt preserve the original draft, proposal, digest and receipts instead of repeating creation.

Syntax, required arguments and defaults come from `twig seed links --help`.
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
twig seed links --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
