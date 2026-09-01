# Views

Read-only rendering commands that project the workspace cache into a shape you can scan quickly. These are the "just show me my work" surfaces: they never propose ADO changes on their own, and any live update flows through `--refresh`.

The `views` group is intentionally small. It is the top-level place to see what is on your plate right now, without traversing the [work-items](../work-items/README.md) card, the [workspace](../workspace/README.md) dashboard, or the [navigation](../navigation/README.md) tree explicitly.

## Commands

| Command | Summary | Stability |
| --- | --- | --- |
| [`twig sprint`](./sprint.md) | Show sprint items grouped by assignee. Defaults to your items; `--all` for the full team. | stable |
| [`twig tree`](./tree.md) | Hidden backward-compat alias that routes to `twig show --tree`, or to `twig workspace --tree` when `--all` is set. | stable (hidden) |

## Notes

- `twig sprint` is the only *dedicated* view command. It reuses `WorkspaceCommand` under the hood with a sprint-layout flag (`src/Twig/Program.cs:1270-1271`), so its rendering, output formats, and refresh semantics track the workspace command.
- `twig tree` is exposed with `[Hidden]` (`src/Twig/Program.cs:676-680`) and remains accepted purely to keep older muscle memory working. It has no behavior of its own — it fans out to `show` or `workspace` based on the `--all` flag. New scripts should invoke the canonical commands directly.
