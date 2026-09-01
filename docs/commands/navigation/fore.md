---
command: fore
group: navigation
summary: Deprecated alias for `nav fore`.
stability: stable
mutates: local
---

# `twig fore`

Deprecated alias for [`twig nav fore`](./nav-fore.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:777-780`), so it does not appear in `twig --help` and
is not surfaced in tab-completion. The `--output` option has no short `-o`
alias in the alias form.

**Prefer `twig nav fore` in new scripts and documentation.**

## Synopsis

```
twig fore [--output <human|json|minimal>]
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

Identical to [`nav fore`](./nav-fore.md): both invocations delegate to
`NavigationHistoryCommands.ForeAsync` (`src/Twig/Program.cs:780`,
`src/Twig/Program.cs:741`). In particular, it bypasses `SetCommand` and does
not record a new history entry — it only moves the cursor.

## Examples

Step forward with the legacy verb:

```
$ twig fore
#4110 Task — Preflight for `batch` op class detection [Doing]
```

Refuse to step forward past the newest entry:

```
$ twig fore
error: Already at newest entry in navigation history.
```

## Exit codes and failure modes

Identical to [`nav fore`](./nav-fore.md).

|Condition|Result|
|---|---|
|Cursor moved forward and active context updated|`0`|
|Cursor already at newest entry|`1`|

## See also

- [`nav fore`](./nav-fore.md)
- [`back`](./back.md)
- [`nav history`](./nav-history.md)
