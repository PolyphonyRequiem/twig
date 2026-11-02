---
command: seed validate
group: seeds
summary: Validate seeds against publish rules.
stability: stable
mutates: none
---

# `twig seed validate`

Validates one local Core seed draft or every seed in the workspace through the
same preview path used to prepare publication. Reports failures and returns a
non-zero exit if any requested seed fails. Provider metadata may be observed;
validation does not create or update remote work items.

## Synopsis

```
twig seed validate [<id>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`id`|no|Seed ID to validate. Omit to validate every seed in the workspace.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- `CoreCommandAdapter.SeedValidateAsync` reads local drafts from the Core snapshot
  and rejects ambiguous display aliases or cyclic local creation links.
- Drafts are previewed in dependency order. Earlier prepared operations establish
  the context for later local references; an explicit ID selects the result to report.
- Preview evaluates the native schema and explicitly declared Twig policy scope.
  A passing result is not a publication acknowledgement.
- Description rich text is not passed through the retired legacy
  Description-origin-paragraph parser. Core ADO creation sends the explicitly
  authored field value, rather than injecting an origin marker into it.
- Output contains a per-seed `results` table and `passed`/`total` counts. An empty
  workspace succeeds with zero rows; an unknown explicit seed is an error.
- Any requested failing seed returns `1`; all requested seeds passing returns `0`.

## Examples

Validate every seed:

```
$ twig seed validate
#-42 PASS
#-43 FAIL  missing System.Title; parent chain unresolved
1 of 2 seeds passed.
```

Validate a single seed as JSON:

```
$ twig seed validate -42 -o json
{"kind":"seedValidation","results":[{"id":-42,"passed":true,"failures":""}],"passed":1,"total":1}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Every requested seed passed, or there were no seeds to validate.|`0`|
|One or more seeds failed at least one rule.|`1`|

## See also

- [`seed publish`](./seed-publish.md) — the operation this gates; publication still requires Core preparation and admission.
- [`seed view`](./seed-view.md) — visual overview alongside completeness ratios.
- [`seed link`](./seed-link.md) — fix parent-chain issues surfaced here.
