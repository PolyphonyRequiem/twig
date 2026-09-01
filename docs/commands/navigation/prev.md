---
command: prev
group: navigation
summary: Deprecated alias for `nav prev`.
stability: stable
mutates: local
---

# `twig prev`

Deprecated alias for [`twig nav prev`](./nav-prev.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:767-770`), so it does not appear in `twig --help` and
is not surfaced in tab-completion. The `--output` option has no short `-o`
alias in the alias form.

**Prefer `twig nav prev` in new scripts and documentation.**

## Synopsis

```
twig prev [--output <human|json|minimal>]
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

Identical to [`nav prev`](./nav-prev.md): both invocations delegate to
`NavigationCommands.PrevAsync` (`src/Twig/Program.cs:770`,
`src/Twig/Program.cs:729`).

## Examples

Step to the previous sibling using the legacy verb:

```
$ twig prev
● #4110  Task — Preflight for `batch` op class detection [Doing]
```

Emit the previous-sibling record as JSON:

```
$ twig prev --output json
{
  "id": 4110,
  "type": "Task",
  "title": "Preflight for `batch` op class detection",
  "state": "Doing"
}
```

## Exit codes and failure modes

Identical to [`nav prev`](./nav-prev.md).

|Condition|Result|
|---|---|
|Sibling resolved and active context updated|`0`|
|No active work item set|`1`|
|Active item has no parent|`1`|
|Active item is the first sibling and no predecessor link exists|`1`|

## See also

- [`nav prev`](./nav-prev.md)
- [`next`](./next.md)
- [`up`](./up.md)
- [`down`](./down.md)
