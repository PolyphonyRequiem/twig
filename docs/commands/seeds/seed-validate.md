---
command: seed validate
group: seeds
summary: Validate seeds against publish rules.
stability: stable
mutates: none
---

# `twig seed validate`

Runs `SeedValidator` against one seed or every seed in the workspace. Reports which
rules would block a publish and returns a non-zero exit if any seed fails, so this is
safe to gate scripts on. No ADO calls; no local mutations.

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

- Loads the current rule set via `ISeedPublishRulesProvider.GetRulesAsync` (`src/Twig/Commands/SeedValidateCommand.cs:41`).
- With an ID, validates that one seed and reports detailed failures inline (`src/Twig/Commands/SeedValidateCommand.cs:43-56`).
- Without an ID, loads every seed through `IWorkItemRepository.GetSeedsAsync` and every link row through `ISeedLinkRepository.GetAllSeedLinksAsync`, then runs `SeedValidator.Validate` per seed (`src/Twig/Commands/SeedValidateCommand.cs:58-71`).
- The empty-workspace case (no seeds) still succeeds with a distinct message; the machine `results` table is emitted with zero rows (`src/Twig/Commands/SeedValidateCommand.cs:59-63`).
- Machine formats emit a document with a `results` Table (per-seed `passed` plus a comma-joined `failures` column) and `passed`/`total` counts. Human output streams a line per result and finishes with a summary; minimal emits `PASS`/`FAIL` markers plus the summary (`src/Twig/Commands/SeedValidateCommand.cs:74-151`).
- Exit code reflects the aggregate: any failing seed → `1`, all pass → `0` (`src/Twig/Commands/SeedValidateCommand.cs:71`).

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

- [`seed publish`](./seed-publish.md) — the operation this gates. `seed publish --force` skips validation.
- [`seed view`](./seed-view.md) — visual overview alongside completeness ratios.
- [`seed link`](./seed-link.md) — fix parent-chain issues surfaced here.
