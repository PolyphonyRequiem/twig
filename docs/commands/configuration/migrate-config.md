---
command: migrate-config
group: configuration
summary: Split a legacy .twig/config into a committed twig.json and gitignored user prefs.
stability: stable
mutates: local
---

# `twig migrate-config`

One-shot migration that splits the legacy single-file `.twig/config` into a committed
`twig.json` manifest at the repo root plus a gitignored `.twig/config` holding per-user
preferences. Idempotent — re-running converges on the split layout and never re-dirties a
repo that is already migrated. Introduced by AB#3296.

## Synopsis

```
twig migrate-config [--dry-run] [-o|--output human|json|minimal]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|—|—|—|—|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--dry-run`|`bool`|`false`|Preview the changes without modifying any files.|
|`-o`, `--output`|`human` \| `json` \| `minimal`|`human`|Output format.|

## Behavior

- Requires either `.twig/config` or `twig.json` to already exist; otherwise exits `1` with
  `No twig configuration found at '<repo>'. Run 'twig init' first.` on stderr
  (`src/Twig/Commands/MigrateConfigCommand.cs:46-51`).
- Loads the current configuration through `TwigConfiguration.LoadSplitAsync` and computes
  the target bytes for both files via `GetRepoBytesAsync` / `GetUserBytesAsync`
  (`src/Twig/Commands/MigrateConfigCommand.cs:53-79`).
- Writes `twig.json` at the repo root when its target bytes differ from disk. In `--dry-run`
  mode it only records the intended change (`src/Twig/Commands/MigrateConfigCommand.cs:63-73`).
- Rewrites `.twig/config` as user-prefs-only when its target bytes differ from disk. In
  `--dry-run` mode it only records the intended change
  (`src/Twig/Commands/MigrateConfigCommand.cs:80-90`).
- Updates the repo-root `.gitignore` to ignore `.twig/` and remove any stale `!.twig/config`
  negation that would leak user preferences (`src/Twig/Commands/MigrateConfigCommand.cs:92-100`
  and `UpdateGitignore` at `src/Twig/Commands/MigrateConfigCommand.cs:197-230`).
- After a real (non-dry-run) migration that actually changed something, prints suggested
  follow-up shell commands: `git add twig.json .gitignore`, `git rm --cached .twig/config`,
  and a scoped commit message (`src/Twig/Commands/MigrateConfigCommand.cs:102-109`).
- Never auto-runs from `twig sync`; must be invoked explicitly so worktrees and other
  un-migrated repos are not re-dirtied silently
  (`src/Twig/Commands/MigrateConfigCommand.cs:19-20`).
- Machine formats emit a single `configMigrated` document (or `configMigrationNoop` when no
  work was needed) with `changes` and, when applicable, `nextSteps` string arrays
  (`src/Twig/Commands/MigrateConfigCommand.cs:23-27`).

## Examples

Preview a migration without touching disk:

```
$ twig migrate-config --dry-run
Would:
  would write twig.json
  would rewrite .twig/config as user-prefs-only
  would update .gitignore: added .twig/ ignore rule
```

Apply the migration and see JSON output with follow-up steps:

```
$ twig migrate-config -o json
{
  "kind": "configMigrated",
  "changes": [
    "created twig.json",
    "rewrote .twig/config as user-prefs-only",
    "updated .gitignore: added .twig/ ignore rule"
  ],
  "nextSteps": [
    "git add twig.json .gitignore",
    "git rm --cached .twig/config   # if the legacy file was previously tracked",
    "git commit -m \"chore(twig): adopt twig.json split (AB#3296)\""
  ]
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Migration applied or already-converged (no-op)|`0`|
|Neither `twig.json` nor `.twig/config` exists|`1` with stderr error pointing at `twig init`|

## See also

- [`config`](./config.md)
- [`config status-fields`](./config-status-fields.md)
- [`help`](./help.md)
