---
command: link artifact
group: links
summary: Attach a hyperlink or vstfs artifact link to a work item.
stability: stable
mutates: ado
---

# `twig link artifact`

Attach an external artifact to a published work item. `http`/`https` URLs are
stored as `Hyperlink` relations; `vstfs://` URIs (commits, branches, PRs,
builds, and other ADO resources) are stored as `ArtifactLink` relations. Use
it to record the branch, commit, doc, or dashboard that belongs to a work
item — the same underlying operation that `seed publish --link-branch` uses
to attach a git branch after publishing.

## Synopsis

```
twig link artifact <url> [--name <name>] [--id <n>] [-o <format>]
```

## Arguments

|Argument|Required|Description|
|---|---|---|
|`url`|yes|Artifact URL (`http`/`https`) or `vstfs://` URI.|

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
|`--name`|`string?`|`null`|Display name for the link. Rendered in ADO in place of the raw URL.|
|`--id`|`int?`|`null`|Target a specific work item by ID instead of the active item.|
|`-o`, `--output`|`string`|`human`|Output format: `human`, `json`, `minimal`.|

## Behavior

Resolves the target (active by default, or `--id`), then calls
`IAdoWorkItemService.AddArtifactLinkAsync` with the URL and optional name
(`src/Twig/Commands/ArtifactLinkCommand.cs:47`). The service returns a boolean
indicating whether the link already existed; the command renders
`artifactAlreadyLinked` in that case and `artifactLinked` otherwise, both with
exit code `0` (`src/Twig/Commands/ArtifactLinkCommand.cs:79`). Unlike the
edge-based verbs, the local link cache is not resynced by this command — the
artifact relation is written directly to ADO.

## Examples

Attach a doc URL to the active item:

```
$ twig link artifact https://example.com/doc --name "Design doc"
Linked https://example.com/doc to #1234.
```

Attach a git commit to a specific item, JSON output:

```
$ twig link artifact vstfs:///Git/Commit/proj/repo/abc123 --id 42 -o json
{
  "kind": "artifactLinked",
  "itemId": 42,
  "url": "vstfs:///Git/Commit/proj/repo/abc123",
  "alreadyLinked": false,
  "message": "Linked vstfs:///Git/Commit/proj/repo/abc123 to #42."
}
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Artifact linked, or already linked|`0`|
|Active/target item not found in cache|`1`|
|ADO rejected the link (invalid URL, auth, network)|`1`|

## See also

- [`link parent`](./link-parent.md)
- [`link related`](./link-related.md)
- [Group overview](./README.md)
