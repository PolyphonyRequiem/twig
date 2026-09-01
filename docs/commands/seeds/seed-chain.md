---
command: seed chain
group: seeds
summary: Create a chain of successor-linked seeds under a shared parent.
stability: stable
mutates: local
---

# `twig seed chain`

Creates a chain of local seeds, each linked to the previous one with a `successor`
edge. Useful when you know the ordered breakdown of a piece of work up front — a
comma-separated string of titles builds the whole chain in one shot; omitting the
argument opens an interactive prompt that reads a title per line until you press
enter on an empty prompt.

## Synopsis

```
twig seed chain [<comma-separated-titles>] [--parent <id>] [--type <type>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`titles`|no|Seed titles, comma-separated. Quote the string so the shell doesn't split it. Omit to enter interactive mode.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--parent`|int|`null`|Parent work item ID for the chain. Defaults to the active work item.|
|`--type`|string|`null`|Work item type applied to every seed in the chain.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Resolves the parent through `ActiveItemResolver`: an explicit `--parent` is looked up with `ResolveByIdAsync`; otherwise the active item is used. Unresolved / unreachable parents fail with a message tailored to which path was taken (`src/Twig/Commands/SeedChainCommand.cs:56-69`).
- With an explicit `titles` argument, the string is split on commas and each entry becomes one seed title. Without it, the command reads titles from stdin one line at a time, suppressing the prompt when stdout is redirected (`src/Twig/Commands/SeedChainCommand.cs:88-105`).
- Each seed is created through `SeedFactory.Create` with a fresh staged identity and persisted via `IWorkItemRepository.SaveAsync`. A creation error emits the partial chain summary on stderr and exits `1` — earlier seeds stay in the cache (`src/Twig/Commands/SeedChainCommand.cs:107-121`).
- **Explicit vs inferred parent.** When `--parent` was passed, a parent-child link row is written in addition to `WorkItem.ParentId`, matching the `seed new` "chosen" signal. When the parent is inferred, the link row is omitted so `seed validate` can flag it (`src/Twig/Commands/SeedChainCommand.cs:129-133`).
- Between seeds `n-1` and `n`, a `successor` link row is added: `previous.Id → new.Id` (`src/Twig/Commands/SeedChainCommand.cs:135-140`).
- Human output streams a `#<id> <title>` line per created seed then a final `Created N seeds: #-42 → #-43 → #-44` summary; machine formats emit a `seedChainCreated` document with a `seeds` Table plus `count` and `chain` fields (`src/Twig/Commands/SeedChainCommand.cs:142-198`).
- An empty run (no titles entered) is a no-op with a `seedChainEmpty` record and exit `0` (`src/Twig/Commands/SeedChainCommand.cs:147-162`).

> Note: bare `twig seed chain` with no argument and no active parent is the same failure mode as running any command that needs an active item — use `--parent <id>` or `twig set <id>` first.

## Examples

Build a three-step chain from explicit titles:

```
$ twig seed chain "design api,build api,cover api" --type Task
  #-42 design api
  #-43 build api
  #-44 cover api
Created 3 seeds: #-42 → #-43 → #-44
```

Interactive chain under a specific parent:

```
$ twig seed chain --parent 5678 --type Task
Seed title (empty to finish): investigate flake
Seed title (empty to finish): stabilise flake
Seed title (empty to finish):
Created 2 seeds: #-45 → #-46
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Chain created (including the empty-input no-op).|`0`|
|Parent not resolvable, invalid `--type`, or a seed-factory error mid-chain.|`1`|

## See also

- [`seed new`](./seed-new.md) — create a single seed.
- [`seed link`](./seed-link.md) — add links after the fact instead of during creation.
- [`seed validate`](./seed-validate.md) — check the chain before publishing.
- [`seed publish`](./seed-publish.md) — publish the whole chain in dependency order.
