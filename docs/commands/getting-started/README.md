# Getting Started commands

These commands cover the first-run workspace bootstrap and the round-trip between the local cache and Azure DevOps that keeps it fresh.

| Command | Summary | Mutates |
| --- | --- | --- |
| [`twig init`](./init.md) | Initialize a new Twig workspace at the invocation git-worktree root. | local |
| [`twig sync`](./sync.md) | Flush pending changes to ADO then refresh the local cache. | ado |
| [`twig refresh`](./refresh.md) | Deprecated alias — routes through `twig sync --pull-only`. | local |

`twig refresh` is a hidden, deprecated alias retained for backwards compatibility. It forwards to `SyncCommand.ExecuteAsync(pullOnly: true)`, so the flush phase is never entered. New scripts should call `twig sync --pull-only` directly.
