---
command: process description
group: process
summary: Describe the admitted Core schema with explicit scope and gaps.
stability: stable
mutates: none
---

# `twig process description`

Project the same admitted provider-discovered schema used by `twig process`.
This is **`core-schema-1`**, scope **`provider-discovered-core-schema`**, not the
legacy complete process descriptor 0.1. Consumers must select the version/scope
explicitly rather than assuming old layout or behavior completeness.

```
twig process description [<type>] [-o human|json|minimal] [--out <path>]
twig process description <type> --sections fields,requirements [--fields <refs>] -o json
```

Omit the type for all types, including hidden types. Canonical type references
are preferred; unambiguous provider-supplied native display names are accepted.
JSON aliases `json-full` and `json-compact` also carry the complete selected Core
projection. Human/minimal renderings are for inspection, not typed interchange.

The document carries `descriptorVersion`, `scope`, `schemaFingerprint`,
`knownGaps` and `types`. Each type has discovered states, exact transition edges,
fields and rules. Rule rows preserve the target `field`, `action`, `conditions`,
typed `values` and `disabled` flag. Each condition retains `kind`, `field` and its
typed `value`; an absent operand differs from a typed Null. Provider gaps are
retained, including unsupported native rules, rather than represented as completeness.
Rules are evidence, not an item-specific gate evaluation or permission to transition.

Native form layout, colors, category membership, general display-name metadata
and behavior metadata are outside this projection and explicitly disclosed as gaps.
There is no invented empty layout or external-automation guarantee.

`--sections fields` selects field definitions; `requirements` selects rule rows.
States and transitions remain available in either compact projection. `--fields`
filters field definitions and rule **targets**, not their condition operands.
Unknown sections/fields refuse instead of silently omitting requested evidence.
Compact requests require an explicit type and a JSON format.

For example:

```
twig process description -o json --out process-schema.json
twig process description Custom.Work --sections requirements --fields Custom.Evidence -o json
```

Substitute actual discovered references. `--out` renders completely before
atomically replacing the file; a failed metadata read preserves an existing file.
No capture timestamp is added, so unchanged discovered evidence can be diffed.
No work-item queries, sync, mutation flushing or ADO writes occur.

The command requires an explicitly initialized, pinned Core workspace and has no
target override options. Discovery/admission, unknown-type and file-write failures
exit 1. Invalid selectors/filters, non-JSON compact requests and `-o ids` exit 2.
Errors are structured for JSON formats.

## See also

- [`twig process`](process.md)
- [`twig process <type>`](process-type.md)
- [`twig process layout`](process-layout.md)
