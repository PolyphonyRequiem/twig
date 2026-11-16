# Local seeds

| Command | Purpose |
| --- | --- |
| [`seed new`](seed-new.md) | Stage a typed UUID-backed local draft |
| [`seed view`](seed-view.md) | Inspect local seeds |
| [`seed edit`](seed-edit.md) | Retained ADO editor convenience |
| [`seed chain`](seed-chain.md) | Retained ADO ordered creation intent |
| [`seed link`](seed-link.md), [`unlink`](seed-unlink.md), [`links`](seed-links.md) | Local creation-link intent, not provider acknowledgements |
| [`seed validate`](seed-validate.md) | Evaluate publication prerequisites |
| [`seed publish`](seed-publish.md) | Human explicit preview/confirmation and publication |
| [`seed reconcile`](seed-reconcile.md) | Inspect original publication evidence, never blind create replay |
| [`seed discard`](seed-discard.md) | Discard eligible local unpublished intent |

Use the actual returned negative alias/UUID, not a predicted `-1`.
Machine creation must choose `--parent` or `--no-parent`; GitHub and Twig-native
creation require `--no-parent`. Structured publication is refused.
Bare `twig seed` is retired; use `seed new`.

Read [drafting and publication](../../features/seeds-and-publishing.md) before
handling interrupted creates.
