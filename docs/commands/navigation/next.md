---
command: next
group: navigation
summary: Deprecated alias for `nav next`.
stability: stable
mutates: local
---

# `twig next`

Deprecated alias for [`twig nav next`](./nav-next.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:762-765`), so it does not appear in `twig --help` and
is not surfaced in tab-completion. The `--output` option has no short `-o`
alias in the alias form
(`C:/Users/dangreen/projects/_briefs/twig-docs-help/next.txt`).

**Prefer `twig nav next` in new scripts and documentation.**

## Synopsis

```
twig next [--output <human|json|minimal>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
| — | — | — |

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--output`|`string`|`human`|Output format for the resulting active-item render.|

## Behavior

Identical to [`nav next`](./nav-next.md): both invocations delegate to
`NavigationCommands.NextAsync` (`src/Twig/Program.cs:765`,
`src/Twig/Program.cs:723`).

## Examples

Advance to the next sibling using the legacy verb:

```
$ twig next
● #4111  Task — Preflight retry telemetry [To Do]
```

Combine with `--output minimal` for prompt integration:

```
$ twig next --output minimal
4111
```

## Exit codes and failure modes

Identical to [`nav next`](./nav-next.md).

|Condition|Result|
|---|---|
|Sibling resolved and active context updated|`0`|
|No active work item set|`1`|
|Active item has no parent|`1`|
|Active item is the last sibling and no successor link exists|`1`|

## See also

- [`nav next`](./nav-next.md)
- [`prev`](./prev.md)
- [`up`](./up.md)
- [`down`](./down.md)
