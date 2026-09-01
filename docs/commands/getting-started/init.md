---
command: init
group: getting-started
summary: Initialize a new Twig workspace at the current git-worktree root.
stability: stable
mutates: local
---

# `twig init`

`twig init` bootstraps a Twig workspace under the current git-worktree root: it detects the worktree anchor, creates the `.twig/` tree, writes the split `twig.json` + user-preference config, initializes the per-workspace SQLite cache, and captures the process description needed for downstream commands. Reach for it the first time you point Twig at an ADO org/project, or when you need to rebuild a workspace from scratch with `--reinitialize`.

## Synopsis

```
twig init [<org>] [<project>] [flags]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<org>` | conditional | Azure DevOps organization, positionally. Required unless `--org` is supplied. The shipped examples use the positional spelling `twig init <org> <project>`. |
| `<project>` | conditional | Azure DevOps project, positionally as the second argument. Required unless `--project` is supplied. |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `--org <string>` | string | none | Azure DevOps organization name (e.g. `contoso`). Named alternative to the positional argument. |
| `--project <string>` | string | none | Azure DevOps project name. Named alternative to the positional argument. |
| `--team <string>` | string | project default team | Team name within the project. |
| `--git-project <string>` | string | same as `--project` | ADO project that hosts the git repository, when different from the work-item project. |
| `--force` | bool | `false` | Overwrite an existing workspace configuration in place. |
| `-o`, `--output <string>` | enum | `human` | Output format: `human`, `json`, `minimal`. |
| `--sprint <string>` | string | none | Sprint expression(s) to subscribe to (e.g. `@current`, `@current-1`). Semicolon-separated for multiple. Each expression is parsed by `IterationExpression.Parse` before any filesystem write. |
| `--area <string>` | string | none | Area path(s) to filter by (e.g. `Project\Team`). Append `:exact` for an exact match. Semicolon-separated for multiple. |
| `--reinitialize` | bool | `false` | Archive an existing `.twig/` tree to `.twig-legacy-<timestamp>/` and start clean. This is the design §7 supported legacy-recovery path — there is no in-place migration. |

## Behavior

`init` refuses to write anything until every input-only check has passed (`src/Twig/Commands/InitCommand.cs:132-255`):

1. **Worktree anchor detection.** `WorktreeAnchorDetector.TryDetect` runs first; any failure (non-git checkout, bare repo, detached path) aborts before any filesystem mutation (`src/Twig/Commands/InitCommand.cs:142-146`).
2. **Root enforcement.** The invocation directory must be the git worktree root, verified via `git rev-parse --show-prefix` (empty at the root on every platform). Nested invocations are refused with `Managed init refused: invocation directory ... is not the git worktree root ...` (`src/Twig/Commands/InitCommand.cs:154-164`).
3. **`.twig/` scoping.** The workspace is always created at `<worktree-root>/.twig/`. A nested repo can never rewrite an ancestor's `.twig/` — this is the defect §3.1 fixes (`src/Twig/Commands/InitCommand.cs:169-170`).
4. **Repo-manifest preservation.** If a tracked `twig.json` already lives at the target, the split configuration is loaded and merged; conflicting org/project/team coordinates or `--git-project`/`--sprint`/`--area` overrides are rejected before mutation (`src/Twig/Commands/InitCommand.cs:174-216`).
5. **Sprint/area validation.** `--sprint` and `--area` are parsed and validated eagerly so a bad flag returns 1 without touching disk (`src/Twig/Commands/InitCommand.cs:222-255`).
6. **Managed init transaction.** Filesystem writes are recorded in `InitRollbackJournal`; a failure at any later step restores overwritten files byte-for-byte and deletes anything created this run (`src/Twig/Commands/InitCommand.cs:905-965`).
7. **Telemetry.** Emits a `CommandExecuted` event with `command=init`, `exit_code`, `output_format`, `had_global_profile`, and generic `duration_ms` / `field_count` metrics — no org, project, or template names ever leave the process (`src/Twig/Commands/InitCommand.cs:116-128`).

On success `.twig/{org}/{project}/twig.db` exists, `twig.json` records the coordinates, and `.twig/` is appended to `.gitignore` (SEC-001).

## Examples

Initialize a workspace against an org and project using the positional spelling:

```
$ twig init contoso Fabrikam
Initialized Twig workspace for contoso/Fabrikam.
Cache: .twig/contoso/Fabrikam/twig.db
```

Initialize a workspace, subscribing to the current and previous sprint and filtering to a single area path with exact matching:

```
$ twig init --org contoso --project Fabrikam \
    --sprint "@current;@current-1" \
    --area "Fabrikam\Team A:exact"
Initialized Twig workspace for contoso/Fabrikam.
Subscribed sprints: @current, @current-1
Area filters: Fabrikam\Team A (exact)
```

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Success | `0` |
| Missing org or project (positional or named) | `1` — prints `error: Usage: twig init <org> <project>, or twig init --org <org> --project <project>` (`src/Twig/Program.cs:517-521`) |
| Not inside a git worktree | `1` — `Managed init refused: not-a-git-worktree` |
| Invocation directory is not the worktree root | `1` — `Managed init refused: invocation directory ... is not the git worktree root ...` |
| Existing tracked `twig.json` conflicts with supplied coordinates | `1` — coordinate-conflict error message |
| Existing tracked `twig.json` conflicts with `--git-project`, `--sprint`, or `--area` overrides | `1` — override-conflict error message |
| Invalid `--sprint` expression | `1` — `Invalid sprint expression '<expr>': <parse error>` |
| Invalid `--area` path | `1` — `Invalid area path '<path>': <parse error>` |
| Managed-init transaction failure | `1` — `InitRollbackJournal` restores overwritten files and removes anything created this run |

## See also

- [`twig sync`](./sync.md)
- [`twig refresh`](./refresh.md)
