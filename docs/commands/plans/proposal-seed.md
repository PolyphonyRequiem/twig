---
command: proposal seed
group: plans
summary: Describe a staged seed's identity and fingerprint for proposal authoring.
stability: stable
mutates: none
---

# `twig proposal seed`

Reports the identity and fingerprint of a currently-staged seed so a
proposal author can paste a stable descriptor into a proposal v1 file.
This is a read helper for the authoring path — it does not mutate the seed
store and does not talk to ADO.

## Synopsis

```
twig proposal seed --id <negative-alias> [-o human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--id`|int|_none_|Negative display alias of a currently-staged seed.|
|`-o`, `--output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Delegates to `IPlanLifecycleService.DescribeSeedAsync`
(`src/Twig/Commands/PlanCommand.cs:173-189`). The lifecycle service resolves
the negative alias to the staged seed, then emits its identity and
fingerprint — a stable pair a proposal file can quote to refer to the seed
without depending on unstable transient state.

- **`--id` must be negative.** A positive id, an unknown alias, or an
  already-published seed returns exit 1 with `proposalSeedNotFound`
  (`src/Twig/Commands/PlanCommand.cs:181-186`). A missing `--id` is a
  usage error and returns exit 2.
- **No mutation.** The seed itself is untouched; only its descriptor is
  read out.

## Examples

Human descriptor:

```console
$ twig proposal seed --id -42
seed: -42
fingerprint: 8c1e…9dfa
```

JSON descriptor to paste into a proposal file's `operations` array:

```console
$ twig proposal seed --id -42 -o json
{ "id": -42, "fingerprint": "8c1e…9dfa" }
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Staged seed found; descriptor emitted.|`0`|
|`--id` refers to no currently-staged seed (positive id, unknown alias, or already published).|`1`|
|`--id` omitted.|`2`|

## See also

- [`proposal validate`](proposal-validate.md) — checks proposal files that quote seed descriptors.
- [`plan seed`](plan-seed.md) — deprecated alias.
