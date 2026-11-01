---
command: process
group: process
summary: List dynamically discovered types from the admitted Core workspace.
stability: stable
mutates: none
---

# `twig process`

List types from the pinned Core workspace's live provider-discovered schema.
Process vocabulary is discovered, never inferred from a template.

```
twig process [-o human|json|minimal] [--include-hidden] [--refresh]
twig process <type> [-o json]
```

`--include-hidden` includes types marked hidden by discovery. Naming a type
explicitly describes it even when hidden. `--refresh` remains accepted, but every
Core invocation already discovers live metadata. There is no second metadata
cache, work-item query, sync, pending flush or write.

JSON and its `json-full`/`json-compact` aliases carry `types` (canonical `typeName`,
`stateCount`, `isHidden`), `totalTypes`, `schemaFingerprint` and provider `knownGaps`.
Child-type counts, colors, icons and category membership are not in Core schema;
they are not fabricated. `ids` is refused with exit 2.

For example:

```
twig process -o json
twig process --include-hidden --refresh -o minimal
```

An admitted, explicitly initialized Core workspace is required. Legacy/unpinned
schema inspection refuses with initialization guidance. `--org` and `--project`
remain parser options, but pinned Core overrides explicitly refuse before HTTP;
use a fresh isolated workspace initialized for the intended target. A half override
or a conflict with the manifest also refuses rather than substituting ambient scope.

Successful discovery exits 0 (including an empty visible list when all types are
hidden). Unknown types or discovery/admission failures exit 1, with structured
errors for JSON output and no successful partial schema.

## See also

- [`twig process <type>`](process-type.md)
- [`twig process description`](process-description.md)
- [`twig process layout`](process-layout.md)
- [`twig states`](states.md)
