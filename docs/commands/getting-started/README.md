# getting-started commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`init`](init.md) | Initialize one fresh native Core workspace at .twig/core.db. No legacy cache, migration or reset. |
| [`sync`](sync.md) | Save retained remote drafts through the proposal journal, then pull the selected Bench. |
| [`refresh`](refresh.md) | Refresh the local cache from the active provider. Routes through sync --pull-only. |
