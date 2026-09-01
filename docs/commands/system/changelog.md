---
command: changelog
group: system
summary: Display recent release notes from GitHub Releases without applying any update.
stability: stable
mutates: none
---

# `twig changelog`

Fetches the most recent release entries from the twig GitHub repository and
prints each one as a markdown block with a `## <tag> (<date>)` heading
followed by the release body verbatim. Reach for it when you want to know
what a specific version added without triggering `twig upgrade`, or when
you are scripting a "what's new since I last upgraded" workflow.

## Synopsis

```
twig changelog [--count <n>] [-o <format>]
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
| `--count <n>` | int | `5` | Number of releases to display. Values below `1` are rejected; values above `100` are clamped to `100` (`src/Twig/Commands/ChangelogCommand.cs:31-38`). |
| `-o, --output <format>` | string | `human` | Output format. Applies to the "No releases found." record and error framing; the release bodies themselves are always emitted raw so their markdown passes through terminals and pipes unchanged. |

## Behavior

`twig changelog` calls `IGitHubReleaseService.GetReleasesAsync(count, ct)`,
which issues a `GET` against
`https://api.github.com/repos/<repo>/releases?per_page=<count>` with a
`User-Agent: twig-cli` header
(`src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs:28-32,70-72`). Any
exception during the fetch is surfaced as "Failed to fetch releases: …" on
stderr and the command exits `1`
(`src/Twig/Commands/ChangelogCommand.cs:45-49`).

When the API returns an empty list, the command renders a `noReleasesFound`
record with the message "No releases found." and exits `0`. The record shape
adapts to the requested format: a JSON record for `json` / `json-full` /
`json-compact` / `ids`, a plain text node for `minimal`, and an informational
text node for `human` (`src/Twig/Commands/ChangelogCommand.cs:51-67`).

For each release the command prints:

1. A blank line separator between entries (skipped before the first one).
2. `## <tag> (<yyyy-MM-dd date or "unknown date">)`.
3. A blank line.
4. The release body, trimmed of trailing whitespace, or `(No release notes.)`
   when the body is empty (`src/Twig/Commands/ChangelogCommand.cs:69-83`).

The bodies are emitted verbatim on stdout — they are upstream markdown, not
twig-formatted output. This makes `twig changelog | less`,
`twig changelog | glow`, and similar pipes render cleanly, but it also means
that JSON output does not wrap the release bodies in a structured envelope;
downstream JSON consumers should treat the release listing as an
informational stream, not as data.

`changelog` never mutates local files, does not touch the workspace, and does
not call ADO.

## Examples

Print the last five releases as markdown:

```
$ twig changelog
## 1.5.0 (2026-08-30)

- Added `workspace area sync` (#1234)
- Fixed 403 loop after tenant switch (#1240)

## 1.4.2 (2026-08-14)

- Hardened token cache path expansion on WSL.

...
```

Pull the last release only, into a pager:

```
$ twig changelog --count 1 | less
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
| One or more releases fetched and printed | `0` |
| Upstream repository has no releases | `0` with a `noReleasesFound` record |
| `--count` below `1` | `1` with "count must be at least 1." |
| Fetch failed (network, rate limit, 5xx from GitHub) | `1` with "Failed to fetch releases: …" |

## See also

* [`upgrade`](upgrade.md) — apply the release these notes describe.
* [`version`](version.md) — confirm which entry corresponds to your build.
* [System commands group](README.md)
