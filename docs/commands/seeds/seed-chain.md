---
command: seed chain
group: seeds
summary: Stage explicit ADO ordered seed creation intent.
stability: stable
mutates: local
---

# `twig seed chain`

```sh
twig seed chain "Investigate,Implement,Verify" --parent 12345 --type <type>
```

## Behavior

Requires explicit `--parent` and `--type`. A single quoted comma-separated
positional value supplies titles; absent titles can be read interactively.
Each retained seed captures dependency intent on its predecessor through the
discovered relation. Native UUIDs and returned aliases are not sequential IDs.
GitHub chain creation is not qualified.

## Effects

Stages local drafts/creation links and can acquire schema/parent evidence.
No provider creation. If a later title fails, already staged earlier seeds
remain; the command is not one all-or-nothing transaction.

## Failure modes

Missing required scope, unsupported dependency schema, invalid title/type/parent,
or native admission failure returns nonzero. Inspect retained seeds before retrying.
