---
command: ws
group: workspace
summary: Short alias for the static Core Bench workspace read.
stability: preview
mutates: local-on-explicit-refresh
---

# `twig ws`

`ws` delegates to the same owner and accepts the same flags as
[`twig workspace`](workspace.md). It reads cached selector membership and all
protected drafts, acquires actual provider schema metadata, and does not change
the current Bench or read implicit active context. The default is a static table.

```powershell
twig ws
twig ws --bench Focused -o json
twig ws --refresh --expect-bench "<current-bench-guid>" -o ids
```

See the canonical command for the `core-bench-workspace-v1` document, typed
cached/proposed field arrays, permanent UUID mapping, missing-scope refusals and
explicit pull-only refresh. `--flat`, `--no-live` and `--view table` are accepted;
legacy tree/team/browser/captured-origin shapes are explicitly unsupported,
not silently rendered with different scope.

Success returns 0, scope/schema/storage refusals return 1, and invalid or
unsupported arguments return 2. JSON errors are one object on stderr with
empty stdout.
