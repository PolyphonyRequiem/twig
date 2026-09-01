---
command: version
group: system
summary: Print the installed twig version.
stability: stable
mutates: none
---

# `twig version`

Prints the installed twig version — the informational version baked into the
assembly at build time — and exits `0`. Reach for it in scripts, bug reports,
and when you want to confirm which build is on `PATH` without invoking any
network or workspace machinery.

## Synopsis

```
twig version
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
| — | — | — | — |

## Behavior

`twig version` reads
`typeof(TwigCommands).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), …)`
and writes the first entry to stdout followed by a newline
(`src/Twig/Program.cs:1321-1325`, `src/Twig/Program.cs:1407-…`).

The same version string is printed by the top-level `twig --version` and
`twig -v` shortcuts (`src/Twig/Program.cs:127-130`), so the two are
interchangeable in scripts.

Although the group-level help lists `-o, --output` for many system commands,
`twig version` does not take an output flag. The help text example
`twig version --output json` is aspirational; the current implementation
prints a single line to stdout regardless of trailing flags and any unknown
argument is rejected by ConsoleAppFramework before it reaches the handler.

`twig version` performs no HTTP calls, opens no files, and reads no
workspace state; it can safely be run on a machine that has never been
signed in or `twig init`-ed.

## Examples

Print the installed version:

```
$ twig version
1.4.2
```

Compare it to what `twig upgrade` sees as latest:

```
$ twig version
1.4.2
$ twig upgrade
Current version: 1.4.2
Checking for updates...
Already up to date (1.4.2)
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| Version attribute present on the assembly (always, in a supported build) | `0` |

## See also

* [`upgrade`](upgrade.md) — check whether a newer version is available.
* [`changelog`](changelog.md) — read the release notes for recent versions.
* [System commands group](README.md)
