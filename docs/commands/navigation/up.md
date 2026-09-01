---
command: up
group: navigation
summary: Deprecated alias for `nav up`.
stability: stable
mutates: local
---

# `twig up`

Deprecated alias for [`twig nav up`](./nav-up.md). Retained for
backward compatibility with scripts that predate the `nav` command group.

The command is marked `[Hidden]` in the CLI registration
(`src/Twig/Program.cs:752-755`), so it does not appear in `twig --help` and is
not surfaced in tab-completion. It also exposes a strictly smaller flag
surface than the canonical form: no short `-o` alias for `--output`, and no
help copy for the option itself
(`src/Twig/Program.cs:752-755`).

**Prefer `twig nav up` in new scripts and documentation.**

## Synopsis

```
twig up [--output <human|json|minimal>]
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
|`--output`|`string`|`human`|Output format for the resulting active-item render. One of `human`, `json`, `minimal`.|

## Behavior

Identical to [`nav up`](./nav-up.md): both invocations delegate to
`NavigationCommands.UpAsync` (`src/Twig/Program.cs:755`,
`src/Twig/Program.cs:705`).

## Examples

Move up one level using the legacy verb:

```
$ twig up
● #4090  Epic — Preflight infrastructure [Doing]
```

Emit JSON via the legacy long option (no `-o` short form is accepted):

```
$ twig up --output json
{
  "id": 4090,
  "type": "Epic",
  "title": "Preflight infrastructure",
  "state": "Doing"
}
```

## Exit codes and failure modes

Identical to [`nav up`](./nav-up.md).

|Condition|Result|
|---|---|
|Parent found and active context updated|`0`|
|No active work item set|`1`|
|Active work item missing from cache|`1`|
|Active work item has no parent|`1`|

## See also

- [`nav up`](./nav-up.md)
- [`down`](./down.md)
- [`next`](./next.md)
- [`prev`](./prev.md)
