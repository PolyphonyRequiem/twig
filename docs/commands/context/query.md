---
command: query
group: context
summary: Run a bounded typed provider query and cache acquired observations.
stability: stable
mutates: local
---

# `twig query`

```sh
twig query --title "authentication" -o json
twig query --state <discovered-state> --top 25
```

## Behavior

Builds a typed Core query from supported predicates using the actual acquired
schema. It is a provider read, not a local substring scan or an unrestricted
WIQL escape hatch. Read `query --help` for current predicates and defaults.

ADO exposes retained type/assignee/path/date filters where the native schema
qualifies them. GitHub CLI queries support title/body/state only and acquire at
most **100 total candidates**, not 100 matches per page. Larger or incomplete
acquisitions refuse before claiming completeness. Unknown or unsupported field
shapes are errors, not ignored filters.

## Effects

Can perform provider HTTP and persist observations. Does not select, pin, assign,
or mutate provider work items.

## Failure modes

Invalid predicates, unsupported provider filters, incomplete acquisition, schema
gaps or denied reads fail explicitly. A bounded query is not a whole-board
certification or a remote snapshot transaction.
