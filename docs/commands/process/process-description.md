---
command: process description
group: process
summary: Project acquired Core schema with fingerprint, scope and explicit known gaps.
stability: stable
mutates: local
---

# `twig process description`

```sh
twig process description -o json
twig process description <type> -o json --sections requirements
twig process description <type> -o json --sections fields --fields <reference>
```

## Behavior

Reports `descriptorVersion: "core-schema-1"`, the provider-discovered scope,
schema fingerprint, types and `knownGaps`. Full JSON is the complete **Core**
projection, not the former complete ADO process/form descriptor.
Layout, display colors/names, categories/behavior and external automation are
outside that projection.

Compact sections are `fields` and `requirements`, requiring an explicit type and
JSON output. Field filters must name actual fields. Omit projections for the full
route. The fingerprint is acquired schema identity, not a server-wide revision.

## Effects

Provider schema reads. Optional `--out <path>` writes a completely rendered file
through temporary replacement; no provider work-item mutation.

## Failure modes

Invalid type/filter/section, unavailable schema or failed file write refuses.
`ids` is unsupported. Read the [feature guide](../../features/process-description.md)
before interpreting requirements as a close decision.
