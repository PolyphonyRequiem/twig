---
command: tree
group: views
summary: Render one qualified cached hierarchy, optionally with explicit refresh.
stability: stable
mutates: local
---

# `twig tree`

```sh
twig tree 12345
twig show 12345 --tree
```

## Behavior

Uses discovered hierarchy relationships for a targeted Core tree.
Pass a positional ID or `--id` in every output format.
`--refresh` explicitly permits provider acquisition;
default presentation is cached. GitHub tree presentation is not qualified.

## Effects

Optional refresh writes observations, never publishes drafts or changes selection.

## Failure modes

Unavailable target/hierarchy evidence or invalid depth refuses.
`--all`, full backlog, live UI and `tree-set` are retired, not fallback routes.
