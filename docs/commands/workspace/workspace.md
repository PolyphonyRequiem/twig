---
command: workspace
group: workspace
summary: Read cached Core Bench membership and protected drafts.
stability: preview
mutates: local-on-explicit-refresh
---

# `twig workspace`

`workspace` (alias `ws`) reads an explicitly initialized Core ADO workspace.
The default is a **static table**, not a live screen, browser envelope or full
backlog tree. An ordinary invocation reads cached items, but acquires **actual
provider schema metadata** because schema is not persisted in the Core snapshot.
It does not query or refresh work items, publish drafts or change selection.

```powershell
twig workspace
twig workspace --bench Focused -o json
twig workspace --bench "<bench-guid>" --expect-bench "<bench-guid>" -o ids
twig workspace --refresh --expect-bench "<current-bench-guid>" -o json
```

## Scope and output

The selected Bench's retained query, pin and subtree selectors are resolved by
Core against cached native items and relationships. Query predicates use typed
cached values, not proposed edits, and do not certify provider-query exhaustion.
All protected drafts are also included, including out-of-Bench edits and
unpublished local seeds. No implicit active item or relatives enter any output.
An active item still appears if explicitly selected by the Bench or carrying debt.
Subtrees require a discovered directed hierarchy and complete cached incident
evidence. Missing pins, unavailable query fields, incomplete subtrees, cycles and
ambiguous Bench names refuse rather than fall back to all cached items.

`--bench` resolves an existing case-insensitive name or stable GUID; it never
changes the interactive current pointer or creates a default. Without it, select
a Bench explicitly using `twig bench list` and `twig bench switch <name>`.
`--expect-bench` guards the resolved identity using its canonical GUID, before
schema HTTP or refresh. A replacement with the same name is not adopted.

All JSON aliases (`json`, `json-full`, `json-compact`) return one document:
`projection: "core-bench-workspace-v1"`, `benchId`, `benchName`, `scope`,
`schema`, and `items`. This is a new bounded CLI projection, **not** legacy
browser completeness evidence. Each row has an integer `id`, nullable local
UUID, `title`, `state`, discovered `type`, `source`, `benchMember`, `hasDraft`,
and separate `cachedFields`/`proposedFields` arrays. Field entries retain reference,
kind and typed value, including explicit null; missing entries stay missing.
Displayed title/state overlay proposed values without changing the original
draft baseline. `source` distinguishes cached observations, local drafts,
cached-plus-proposed values and baseline-plus-proposed debt.

Published local selectors resolve through their permanent mapping in memory;
stored selectors, original UUIDs, proposal bytes, digests and journal rows remain
unchanged. Unpublished locals retain the existing reserved negative UUID alias.
Alias collisions refuse rather than choose another identity. Mapped draft debt
without a cached native observation refuses instead of fabricating verification.
There is no freshness, complete remote membership or Verified-status claim.

`human` is the default table. `minimal` is static tab-separated output;
`ids` emits only member/debt integer IDs, one per line. `--no-live`, `--flat`
and `--view table` are accepted static-table spellings.

## Explicit refresh and unsupported shapes

`--refresh` uses the existing Core Bench pull with a frozen expected current
Bench and shared configured context depths. Active-context expansion is disabled.
It refreshes cached observations while preserving drafts/baselines, mappings,
selectors and journals; it never saves, applies or flushes a proposal.
Refreshing a named **noncurrent** Bench refuses before pulling. Switch it
explicitly first; reads cannot switch it behind the caller's back.
`workspace sync` remains its separate pull-only command.

`--all`, `sprint`, `tree --all`, `--tree`, `--view tree`, unknown views,
`--include-browser`, `--browser-version`, `--expect-binding` and
`--expect-identity` and `--expect-settings` are unqualified legacy shapes and refuse with usage exit 2
before HTTP. Invalid guards likewise return 2. Scope/storage/schema refusals
return 1 with empty stdout and format-aware stderr (`{"error":"..."}` for JSON).
The targeted `show <id> --tree` route is unchanged.

The old workspace aggregate, WorkingSet/SyncCoordinator, live/legacy-human
formatter and BrowserV2 partial ownership have been removed from this owner.
UI layout and browser contracts may be redesigned separately; this does not
authorize deleting shared UI services, history or workspace data, and does not
complete the remaining Core/UI cutover or qualify GitHub CLI workspace reads.
