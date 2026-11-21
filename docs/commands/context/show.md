---
command: show
group: context
summary: Read an explicit cached Core item, with optional provider refresh.
stability: stable
mutates: local
---

# `twig show`

```sh
twig show 12345
twig show --id 12345 --refresh -o json
twig show 12345 -o json --fields System.Title,System.State
twig show --local-id <original-uuidv7> -o json
```

## Behavior

Reads the retained item by positional ID or `--id`. A cache miss fails
unless `--refresh` explicitly permits native lookup/read. Refresh updates the
mirror but does not publish drafts. Missing or conflicting targets refuse.

`--fields` selects exact retained fields; unknown/absent data is not fabricated.
`--sections` is currently refused. An unprojected `--tree` uses the targeted
hierarchy path, which is not GitHub-qualified.

`--local-id` accepts an original canonical lowercase UUIDv7 seed identity or
permanent publication mapping. It is cache-only, JSON-only, and exclusive with
another target, refresh, tree, or field projection. Imported GitHub issues use
numeric native locators, not an arbitrary local UUID registry.

## Effects

Default detail is cached inspection; explicit refresh performs provider reads
and persists observations. Neither changes selection or grants publication.
The output includes identity, revision, cached/seed/dirty indicators, and typed
fields; it is not the former provider-snapshot or captured detail envelope.

## Failure modes

Uncached, ambiguous, invalid, unavailable, or cross-connection targets fail.
An omitted target is a usage error. An unpublished seed cannot be
refreshed. Check the exit code as well as output.
