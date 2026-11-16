---
command: query
group: context
summary: Search using bounded provider-qualified Core queries. GitHub supports title/body/state only, with at most 100 total candidates.
stability: stable
mutates: local
---

# `twig query`

Search using bounded provider-qualified Core queries. GitHub supports title/body/state only, with at most 100 total candidates.

## Behavior

Queries the bound provider/cache within supported limits. GitHub supports only qualified bounded shapes, with 100 total candidates, not general ADO query parity.

Syntax, required arguments and defaults come from `twig query --help`.
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
twig query --help
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
