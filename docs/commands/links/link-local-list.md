---
command: link local list
group: links
summary: Read cached LOCAL edges for an explicit Core item with zero HTTP.
stability: stable
mutates: none
---

# `twig link local list`

```text
twig link local list [<target>] [--id <target>] [-o human|json|json-full|json-compact|minimal|ids]
```

An explicit cached native number, negative seed alias or original canonical
lowercase GUIDv7 is required. Different positional/`--id` values refuse rather
than guessing; there is no active-item fallback. Unknown numbers require an
explicit `show <number> --refresh` outside this command.

List reads the pinned Core snapshot only: **zero total HTTP**, no credential or
schema acquisition, no hidden number fetch, no store/journal/context changes.
It shows active local-origin edges touching the logical target, excludes
tombstones and native-origin observations, and labels provenance `LOCAL`,
`local-only`, `non-native`. Original Verified seed mappings let both UUID and
native number find the same unchanged edge keys.

JSON variants retain typed endpoint strings, kind, local version and deletion
flag. Human/minimal output is static. `ids` emits explicit numeric neighbor
locators (negative aliases for live local seeds) only when every neighbor can be
resolved uniquely from cached evidence; otherwise it explicitly refuses. No
endpoint is silently dropped to make an IDs result look complete.

## Examples

```text
twig link local list 42
twig link local list --id 019a0000-0000-7000-8000-000000000001 -o json
```

## Exit codes

- `0`: cached edges read, including an empty list.
- `1`: unknown/non-live identity, conflicting logical edges or unavailable store.
- `2`: missing/conflicting/invalid target, unsupported output or unavailable
  unique neighbor IDs.
