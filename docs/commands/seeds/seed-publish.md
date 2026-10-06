---
command: seed publish
group: seeds
summary: Publish seeds to Azure DevOps.
stability: stable
mutates: ado
---

# `twig seed publish`

Pushes local seeds to Azure DevOps, turning each negative-ID draft into a real work
item with a positive ID. Publishes in dependency order — parents before children,
predecessors before successors — so cross-seed references translate cleanly. With
`--all`, publishes every seed; with an ID, publishes just that one. `--dry-run`
previews the operation without mutating anything, and `--link-branch` attaches the
newly-created items to a git branch as an ADO artifact link.

This is the **only** command in this group that talks to Azure DevOps.

## Synopsis

```
twig seed publish [<id>] [--all] [--force] [--dry-run]
                  [--link-branch <branch>] [--repo <name>] [-o|--output <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`id`|conditional|Seed ID to publish. Omit when `--all` is passed; required otherwise.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--all`|bool|`false`|Publish every seed in topological dependency order.|
|`--force`|bool|`false`|Skip `seed validate` before publishing.|
|`--dry-run`|bool|`false`|Preview what would be published without calling ADO.|
|`--link-branch`|string|`null`|Link each published work item to this git branch as an ADO artifact link (e.g. `feature/my-branch`).|
|`--repo`|string|`null`|Repository name for branch linking. When omitted, uses the workspace-configured default repository.|
|`-o, --output`|string|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

- Delegates the transactional work to `SeedPublishOrchestrator.PublishAsync` / `PublishAllAsync`. In `--all` mode the orchestrator computes the topological order and publishes seeds one by one, rewriting inbound references to the newly minted ADO IDs as it goes (`src/Twig/Commands/SeedPublishCommand.cs:45-67`).
- Requires either an ID **or** `--all`. Bare `twig seed publish` returns exit `1` with `Specify a seed ID or use --all.` (`src/Twig/Commands/SeedPublishCommand.cs:69-73`).
- When `--link-branch` is set, the branch artifact URI is resolved **once upfront** via `ResolveBranchArtifactUriAsync` — `--repo` chooses `IAdoGitService.GetRepositoryIdByNameAsync`, otherwise the workspace-configured `GetRepositoryIdAsync` — so a bad branch name fails before the publish loop instead of after (`src/Twig/Commands/SeedPublishCommand.cs:42-43,166-197`).
- After publish, `LinkBatchAsync` walks each successful result and calls the ADO link API. Individual link failures are **best-effort**: they are logged to stderr and counted, but do not fail the command. A summary line (`Linked N / failed M to branch <name>`) follows the publish result (`src/Twig/Commands/SeedPublishCommand.cs:49-55,79-83,203-243`).
- `--dry-run` skips branch resolution entirely and returns the orchestrator's simulated results without any writes (`src/Twig/Commands/SeedPublishCommand.cs:166-172`).
- **Active context follow.** If the active work item was one of the published seeds, the active-item pointer is rewritten to the newly assigned positive ID so the next `twig show` still points at the same conceptual item (`src/Twig/Commands/SeedPublishCommand.cs:57-64,85-87`).
- Exit code mirrors the orchestrator: any error in batch mode → `1`; single-seed mode returns `1` when `!IsSuccess` (`src/Twig/Commands/SeedPublishCommand.cs:66,89`).

### Batch preflight diagnostics

`--all` checks seed validation, parent references, dependency cycles, and seed-ID
escapes before creating any ADO work items. A passing `seed validate` is not proof
that batch preflight passed: publishing also checks the graph and field references.
A single-child dry run can still refuse an unpublished parent.

The escape guard recognizes standalone negative integer tokens that match a seed
in the batch, including `-3`, `#-3`, `(-3)`, and references inside HTML or URLs.
Embedded identifier, version, date, and decimal fragments such as `AC-1`,
`v0.97.1-4`, `2026-1-2`, and `-3.5` are legitimate content, not seed references.
Titles and all field values are checked; matching references still stop the batch.
Use seed links for relationships rather than placing local seed IDs in prose.

JSON always includes `preFlightErrors` alongside `results` and `cycleErrors`.
Each preflight entry has the shape
`{"key":"preFlightError","value":"<diagnostic>"}`, consistent with cycle diagnostics.
Human and minimal output show each diagnostic too. A preflight refusal exits `1`
even if `results` is empty; it does **not** say “No seeds to publish.” That message
is reserved for an empty batch with no diagnostics. The same reporting applies
to ordinary publishing and `--dry-run`; legitimate AC labels need no workaround.

## Examples

Publish one seed:

```
$ twig seed publish -42
Published seed #-42 → #7842 (Task)
```

Publish everything and link to a branch:

```
$ twig seed publish --all --link-branch feature/audit-trail
Published seed #-42 → #7842 (Task)
Published seed #-43 → #7843 (Task)
Published seed #-44 → #7844 (Task)
Linked 3, failed 0 to branch feature/audit-trail.
```

Preview an `--all` publish without touching ADO:

```
$ twig seed publish --all --dry-run -o json
{"results":[{"kind":"seedPublishResult","oldId":-42,"newId":0,"title":"Preview seed","status":"DryRun","isSuccess":true,"errorMessage":null}],"cycleErrors":[],"preFlightErrors":[],"createdCount":0,"skippedCount":0,"hasErrors":false}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Publish succeeded, or `--dry-run` completed.|`0`|
|Neither an ID nor `--all` supplied.|`1`|
|Any seed failed to publish (batch: `HasErrors`; single: `!IsSuccess`).|`1`|
|Batch preflight refused (including `--dry-run`).|`1`; every preflight diagnostic is included in the selected output format.|
|Branch link failure|**does not fail the command** — reported in the link summary.|

## See also

- [`seed validate`](./seed-validate.md) — the pre-publish gate. Skipped by `--force`.
- [`seed reconcile`](./seed-reconcile.md) — repair local link tables after a partial publish.
- [`seed view`](./seed-view.md) — inspect what would be published.
- [`seed chain`](./seed-chain.md) — build ordered seed sets that publish cleanly here.
