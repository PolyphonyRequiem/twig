---
command: refresh
group: getting-started
summary: Deprecated alias for `twig sync --pull-only` — refresh the local cache from Azure DevOps.
stability: stable
mutates: local
---

# `twig refresh`

`twig refresh` is a deprecated, hidden alias that refreshes the local cache from Azure DevOps without flushing pending changes. It routes straight through `SyncCommand.ExecuteAsync(pullOnly: true)` — the flush phase is never entered — so behavior is identical to `twig sync --pull-only`. New scripts should call `twig sync --pull-only` directly; this alias is retained purely for backwards compatibility with pre-existing muscle memory and automation.

## Synopsis

```
twig refresh [flags]
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
| `--output <string>` | enum | `human` | Output format: `human`, `json`, `minimal`. |

## Behavior

`twig refresh` is a thin `[Hidden]` wrapper over `SyncCommand.ExecuteAsync` (`src/Twig/Program.cs:1061-1064`):

```
[Hidden]
public async Task<int> Refresh(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    => await services.GetRequiredService<SyncCommand>().ExecuteAsync(output, pullOnly: true, ct);
```

Because it hard-codes `pullOnly: true`:

- The push phase (`IPendingChangeFlusher.FlushAllAsync`) is skipped entirely — pending edits stay dirty on disk.
- The pull phase (`RefreshCommand.ExecuteAsync`) runs and refreshes the working set from ADO into the SQLite cache.
- No push summary is printed; the sync-drop diagnostic on stderr still runs, but with no flush having happened the staged-vs-pushed diff is always zero (`src/Twig/Commands/SyncCommand.cs:54-102`).
- `refresh` is registered in the internal alias list at `src/Twig/Program.cs:1604-1605`; it is `[Hidden]` and does not appear in `twig --help`, only in this reference and its own `twig refresh --help`.

Prefer `twig sync --pull-only` in new automation. The alias will be removed in a future major release; there is no schedule yet.

## Examples

Refresh the local cache without touching pending edits:

```
$ twig refresh
Refreshed working set from contoso/Fabrikam.
```

Equivalent invocation using the non-deprecated spelling:

```
$ twig sync --pull-only
Refreshed working set from contoso/Fabrikam.
```

## Exit codes and failure modes

| Condition | Result |
| --- | --- |
| Refresh (pull) phase succeeds | `0` |
| Refresh (pull) phase exits non-zero | `1` — propagated from `RefreshCommand.ExecuteAsync` |

## See also

- [`twig sync`](./sync.md)
- [`twig init`](./init.md)
