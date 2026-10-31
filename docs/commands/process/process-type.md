---
command: process <type>
group: process
summary: Describe one dynamically discovered work-item type.
stability: stable
mutates: none
---

# `twig process <type>`

Inspect one type in the pinned Core workspace's live schema:

```
twig process <type> [-o human|json|minimal] [--refresh]
```

Use the canonical type reference from `twig process`. An unambiguous native display
name is also accepted when the provider supplies that evidence. Hidden types may
be named explicitly. No process-specific type or state vocabulary is hardcoded.

The result includes `type`, `isHidden`, `stateField`, `states`, `fields`,
`transitions` and `rules`. Fields carry `referenceName`, Core `dataType` and
`isReadOnly`; rules carry actions, conditions, typed operands/values and disabled
status. Transition edges are exactly those discovered, not all possible state pairs.
Human and minimal renderings are for inspection; use JSON (or a JSON alias) to
retain typed nested values.

For example, substituting a reference actually returned by your provider:

```
twig process Custom.Work -o json
twig process Custom.Work --refresh -o minimal
```

`--refresh` is accepted; discovery is already live and never queries work items,
synchronizes caches or flushes mutations. Legacy/unpinned schema inspection and
pinned Core `--org/--project` overrides explicitly refuse; initialize a fresh
isolated workspace for another target.

Success exits 0. Unknown/ambiguous types and admission/discovery failures exit 1.
`-o ids` exits 2. JSON failures are structured errors rather than empty metadata.

## See also

- [`twig process`](process.md)
- [`twig process description`](process-description.md)
- [`twig process layout`](process-layout.md)
