---
command: down
group: navigation
summary: Deprecated alias for `nav down`.
stability: stable
mutates: local
---

# `twig down`

Deprecated alias for [`twig nav down`](./nav-down.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:757-760`), so it does not appear in `twig --help` and
is not surfaced in tab-completion. It exposes fewer help affordances than the
canonical form — the positional argument has no description and the
`--output` option has no short alias
(`C:/Users/dangreen/projects/_briefs/twig-docs-help/down.txt`).

**Prefer `twig nav down` in new scripts and documentation.**

## Synopsis

```
twig down [<idOrPattern>] [--output <human|json|minimal>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`<idOrPattern>`|no|Child work item ID or title substring. Same semantics as [`nav down`](./nav-down.md).|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--output`|`string`|`human`|Output format for the resulting active-item render.|

## Behavior

Identical to [`nav down`](./nav-down.md): both invocations delegate to
`NavigationCommands.DownAsync` (`src/Twig/Program.cs:760`,
`src/Twig/Program.cs:717`).

## Examples

Descend into a specific child by ID:

```
$ twig down 4110
● #4110  Task — Preflight for `batch` op class detection [Doing]
```

Descend into the sole child of the active item:

```
$ twig down
● #4110  Task — Preflight for `batch` op class detection [Doing]
```

## Exit codes and failure modes

Identical to [`nav down`](./nav-down.md).

|Condition|Result|
|---|---|
|Child resolved and active context updated|`0`|
|No active work item set|`1`|
|Active work item missing from cache|`1`|
|No children to descend into|`1`|
|Pattern matched nothing|`1`|
|Multiple matches without interactive renderer|`1`|

## See also

- [`nav down`](./nav-down.md)
- [`up`](./up.md)
- [`next`](./next.md)
- [`prev`](./prev.md)
