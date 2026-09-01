---
command: sync
group: getting-started
summary: Flush pending changes to Azure DevOps then refresh the local cache.
stability: stable
mutates: ado
---

# `twig sync`

`twig sync` is the round-trip between the local Twig cache and Azure DevOps. It first flushes every pending change staged in the workspace's queue up to ADO, then re-pulls the working set to bring the cache back in line. Use it whenever you want your notes and field edits to reach the board and your cache to see everything anyone else committed since the last sync.

## Synopsis

```
twig sync [flags]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| — | — | — |

## Flags

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output <string>` | enum | `human` | Output format: `human`, `json`, `minimal`. |
| `--pull-only` | bool | `false` | Skip the flush phase and only pull (refresh) from ADO. Equivalent to the deprecated `twig refresh` alias. |

## Behavior

`twig sync` runs a two-phase pipeline (`src/Twig/Commands/SyncCommand.cs:26-105`):

1. **Push phase.** Unless `--pull-only` is set, `IPendingChangeFlusher.FlushAllAsync` drains every dirty item in the workspace queue. Any per-item failure is written to stderr as `Flush failed for #<id>: <reason>` and captured in the returned `FlushResult` (`src/Twig/Commands/SyncCommand.cs:35-45`).
2. **Pull phase.** `RefreshCommand.ExecuteAsync` runs unconditionally, pulling the working set from ADO into the local cache (`src/Twig/Commands/SyncCommand.cs:47-48`).
3. **Drop accounting.** The command explicitly compares field changes and notes staged against those actually pushed. Staged-but-not-pushed content is data loss (#251) and is surfaced on stderr as `<N> note(s) and <M> field change(s) were staged but not pushed to Azure DevOps.` — it is never rendered as “nothing to flush” (`src/Twig/Commands/SyncCommand.cs:54-102`).
4. **Human/JSON/minimal rendering.** Human output prints a one-line push summary (`Sync push: <flushed> flushed, <failed> failed.` / `Sync push: nothing to flush.`); `--output json` and `--output minimal` emit structured summaries via `RenderSyncJson` / the render tree (`src/Twig/Commands/SyncCommand.cs:60-93`).
5. **Scope.** Sync currently flushes every dirty item; per-item scoping (`twig sync <id>`) is deferred (T-1342.1 / #1342). See the TODO at `src/Twig/Commands/SyncCommand.cs:24-25`.

`twig refresh` is a hidden, deprecated alias that calls `SyncCommand.ExecuteAsync(pullOnly: true)` — see `src/Twig/Program.cs:1061-1064`.

## Examples

Flush pending edits and refresh the cache:

```
$ twig sync
Sync push: 3 flushed, 0 failed.
Refreshed working set from contoso/Fabrikam.
```

Skip the flush phase — equivalent to the deprecated `twig refresh` alias:

```
$ twig sync --pull-only
Refreshed working set from contoso/Fabrikam.
```

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Push and pull both succeed | `0` |
| Any per-item flush failure | `1` — each failure written to stderr as `Flush failed for #<id>: <reason>` (`src/Twig/Commands/SyncCommand.cs:40-44`) |
| Refresh (pull) phase exits non-zero | `1` — propagated from `RefreshCommand.ExecuteAsync` |
| Staged content not pushed | Exit follows the push/pull result, and stderr surfaces the drop count (`src/Twig/Commands/SyncCommand.cs:95-102`) |

## See also

- [`twig init`](./init.md)
- [`twig refresh`](./refresh.md)
