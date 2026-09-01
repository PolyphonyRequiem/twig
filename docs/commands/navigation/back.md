---
command: back
group: navigation
summary: Deprecated alias for `nav back`.
stability: stable
mutates: local
---

# `twig back`

Deprecated alias for [`twig nav back`](./nav-back.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:772-775`), so it does not appear in `twig --help` and
is not surfaced in tab-completion. The `--output` option has no short `-o`
alias in the alias form.

**Prefer `twig nav back` in new scripts and documentation.**

## Synopsis

```
twig back [--output <human|json|minimal>]
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
|`--output`|`string`|`human`|Output format used for error messages.|

## Behavior

Identical to [`nav back`](./nav-back.md): both invocations delegate to
`NavigationHistoryCommands.BackAsync` (`src/Twig/Program.cs:775`,
`src/Twig/Program.cs:735`). In particular, it bypasses `SetCommand` and does
not record a new history entry — it only moves the cursor.

## Examples

Step backward with the legacy verb:

```
$ twig back
#4102 Task — Wire batch preflight into publish [Doing]
```

Refuse to step back past the oldest entry:

```
$ twig back
error: Already at oldest entry in navigation history.
```

## Exit codes and failure modes

Identical to [`nav back`](./nav-back.md).

|Condition|Result|
|---|---|
|Cursor moved back and active context updated|`0`|
|Cursor already at oldest entry|`1`|

## See also

- [`nav back`](./nav-back.md)
- [`fore`](./fore.md)
- [`nav history`](./nav-history.md)
