---
command: seed publish
group: seeds
summary: Explicitly publish selected seeds through Core proposals and verified mappings.
stability: stable
mutates: ado
---

# `twig seed publish`

```sh
twig seed publish <negative-id> --dry-run
twig seed publish <negative-id>
```

## Behavior

Use the returned negative ID as a positional argument, or use `twig seed publish --all`
to explicitly select all seeds. Human output only; machine
publication is refused. Prepares ordered creation intent through Core with the
original UUIDs, schema, policy, digest and attempts.

`--dry-run` retains the exact unattempted proposal digest without provider
creation. Repeating the preview against unchanged workspace state and provider
observations reuses that digest. The non-dry-run command reuses the retained
proposal when it still matches; otherwise it prepares fresh intent. It
**publishes** through the existing internally digest-bound save path; it is not
an external `--confirm` review prompt.
Review intent before issuing it. For an explicitly confirmed file, use
`proposal apply --file ... --confirm <digest>`.

ADO creation is non-atomic with respect to the reviewed area and iteration IDs.
Twig refuses a remap it detects before dispatch, but the create request sends
paths, not a server-side condition on those IDs. A path can resolve differently
after the last check. If immutable revision readback finds a mismatch, the
creation is unresolved; do not retry it or publish a replacement seed.

ADO creation-bound branch resources use the actual supported native discovery
path. GitHub creation resources/links are not qualified.

## Effects

Publication can create provider items, persist acknowledgements, independently
verify readback, retain permanent UUID mappings, and settle exact drafts.
Even dry-run can retain local journal/resource intent; it is not guaranteed to
be a byte-for-byte read-only workspace operation.

## Failure modes

Invalid scope, dependencies/cycles, unavailable schema/policy, changed admission
or unresolved effects fail. A lost response retains the original fence, drafts
and receipts. Never create a replacement seed or repeat an unknown create.
See [recovery](../../features/seeds-and-publishing.md#interrupted-publication).
