---
command: bench create
group: bench
summary: Create an empty user-wide Bench without selecting it.
stability: stable
mutates: local
---

# `twig bench create`

Create a named, durable Bench in the native user-wide store. It is the same
definition used by the CLI and panel, independent of a repository/worktree's
lifetime. No initialized workspace, provider credentials or work HTTP is needed.

```powershell
twig bench create "Release"
twig bench create "Bugs I own" -o json
```

The new Bench is empty. Nothing is copied, pinned, refreshed or selected
automatically. Use `twig bench switch <name-or-guid>` in an initialized worktree
to select it there without switching another worktree.

`name` must be trimmed, nonempty and at most 120 characters. Names are unique
under Unicode-normalized, case-insensitive comparison. A collision refuses
without modifying the existing Bench.

`-o|--output` supports `human`, JSON variants and `minimal`. Success reports the
new `id`, `name`, `revision` and message. Errors use the selected output format
on stderr: `0` success, `1` conflicting/unavailable state, `2` invalid input.

See [`bench list`](./list.md), [`bench rename`](./rename.md),
[`bench switch`](./switch.md) and [`bench delete`](./delete.md).
