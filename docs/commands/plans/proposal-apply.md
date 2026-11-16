---
command: proposal apply
group: plans
summary: Apply a proposal when --confirm &lt;digest&gt; matches the current file digest exactly. Canonical verb is proposal apply; plan apply is a retained deprecated alias.
stability: stable
mutates: provider
---

# `twig proposal apply`

Apply a proposal when --confirm &lt;digest&gt; matches the current file digest exactly. Canonical verb is proposal apply; plan apply is a retained deprecated alias.

## Behavior

Requires an explicit v3 file and its exact canonical --confirm digest. Native Core rechecks admission, schema, policy, revision and journal evidence. A successful response requires verified effects; indeterminate attempts are never blindly replayed.

Syntax, required arguments and defaults come from `twig proposal apply --help`.
Structured single-item mutations require an explicit `--id`; active selection
is a human convenience, not automation scope. Output and error formats are
declared, never inferred from TTY state.

## Effects

Effect scope: `provider`. Provider effects may publish changes. Local state,
remote publication and verified outcomes are distinct. Do not auto-save,
discard or replay unrelated intent.

## Failures

Invalid arguments, unsupported capabilities, stale identity/revision evidence
and unavailable data are explicit errors, not empty success. Inspect stderr,
exit status and any per-operation outcome before reporting completion.

## Examples

```sh
twig proposal apply --file .twig\proposals\change.json --confirm <exact-preview-digest>
```

Examples use illustrative IDs/names; substitute observed identities and
discovered process values. Commands ending in `--help` inspect syntax only;
mutation examples can publish changes.
