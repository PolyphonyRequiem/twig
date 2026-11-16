---
command: bench switch
group: bench
summary: Select an existing shared Bench in this initialized worktree.
stability: stable
mutates: local
---

# `twig bench switch`

```sh
twig bench switch "Release"
twig bench switch <canonical-guid> --expect-bench <canonical-guid> -o json
```

## Behavior

Selects one existing unambiguous shared Bench by name or canonical GUID.
Requires this worktree's initialized native context. No automatic default is
created; `--expect-bench` refuses replacement under the same name.
Old captured browser binding/identity envelopes are not qualified.

## Effects

Changes only this worktree's local Bench pointer. Shared pins/rules, item
assignment, provider state and other contexts' selections are unchanged.
No automatic pull or draft publication.

## Failure modes

Missing/ambiguous name, changed identity/store or unsupported capture flags
refuse rather than creating/selecting another Bench.
