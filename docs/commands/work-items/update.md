---
command: update
group: work-items
summary: Edit one discovered typed field and immediately save published-item intent through Core.
stability: stable
mutates: ado
---

# `twig update`

```sh
twig update System.Title "New title" --id 12345 -o json
twig update System.Description --file description.md --format markdown --id 12345
```

## Behavior

Resolves the actual writable field/type and converts only supported native
values. Common aliases such as title/state/description map to provider roles;
exact discovered references are preferable for automation.
Use exactly one value source: inline, `--file`, or `--stdin`.
`--append` requires an actually observed text-like field.

Published-item changes stage, preview and save through the same Core proposal
and verified journal path. Seed field edits CAS the original local draft and
do not create a provider item. Name the target with `--id` in every output format.
HTML fields use supported Markdown/raw conversion; native Markdown stays Markdown.

## Effects

Can read provider/schema data and publish immediately for a remote item.
Local seed edits stay local. No automatic account fallback or conflict overwrite.

## Failure modes

Unknown/ambiguous/read-only fields, unsupported typed conversion, incompatible
input, stale intent, unmet rules or uncertain save fail explicitly.
Use a combined operation for coupled field/state requirements.
