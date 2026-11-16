# seeds commands

Current bundled command behavior. Executable help supplies declaration-derived syntax.

| Command | Behavior |
|---|---|
| [`seed new`](seed-new.md) | Create a new local seed work item. |
| [`seed edit`](seed-edit.md) | Edit seed fields in an external editor. |
| [`seed discard`](seed-discard.md) | Discard (delete) a local seed. |
| [`seed view`](seed-view.md) | Show seed dashboard grouped by parent. |
| [`seed link`](seed-link.md) | Create a virtual link between two items (at least one must be a seed). |
| [`seed unlink`](seed-unlink.md) | Remove a virtual link between two items. |
| [`seed links`](seed-links.md) | List virtual links, optionally filtered by item ID. |
| [`seed chain`](seed-chain.md) | Create a chain of linked seeds — interactively or from explicit titles. |
| [`seed validate`](seed-validate.md) | Validate seeds against publish rules. |
| [`seed publish`](seed-publish.md) | Publish seeds to Azure DevOps. Use --all for batch, --dry-run to preview, --force to skip validation, --link-branch to link to a git branch. |
| [`seed reconcile`](seed-reconcile.md) | Reconcile stale seed links and parent references after partial publishes. |
