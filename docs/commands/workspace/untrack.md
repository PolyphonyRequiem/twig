---
command: workspace untrack
group: workspace
summary: Remove exact single/subtree selectors without deleting inherited membership or drafts.
stability: stable
mutates: local
---

# `twig workspace untrack`

```sh
twig workspace untrack 12345 --mode single -o json
twig workspace untrack 12345
```

## Behavior

Removes exact explicit selectors in the selected shared Bench. `--mode single`
or `tree` narrows removal; omitted mode removes both kinds. An absent matching
selector is a successful no-op. Query/ancestor-subtree membership can keep an
item visible. `--expect-bench` is a canonical GUID, not an old decimal storage ID.

Old binding/identity/settings capture flags refuse. GitHub subtree-specific
removal is not qualified. Invalid mode never falls back to both.

## Effects

Shared local selector CAS, no provider mutation or deletion of seed/draft/journal
evidence. Other Benches and inherited membership are preserved.

## Failure modes

Missing/changed selection, unavailable target or unsupported capture fails
without changing a replacement Bench.
