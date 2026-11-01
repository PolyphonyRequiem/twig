# Links commands

Manage relationships and artifact links. The published-item verbs operate on
Azure DevOps work items — hierarchy (parent/child), non-hierarchy relations
(`predecessor`, `successor`, `related`), and artifact links (URLs and
`vstfs://` URIs). The `link local` verbs use native numeric locators or original
Core seed UUIDs/negative aliases for explicit LOCAL Related links. Virtual links between local
seeds live under `seed link` / `seed unlink` instead.

Published relationship verbs require an explicitly initialized Core ADO workspace
and use one proposal/journal workflow. Success requires separately verified native
effects, not best-effort resync. Reparent and relationship comments remain
unsupported gaps, refused before HTTP. Artifact and local-link routes have separate
qualification status; their presence here does not imply a complete Core/UI cutover.
`link local related` and `link local unrelate` mutate only Twig's durable
local relationship store; they never write or translate the edge to a provider.
Mutations may acquire native schema/observations; only `link local list` guarantees
zero total HTTP.

## Commands

|Command|Summary|Mutates|
|---|---|---|
|[`link parent`](./link-parent.md)|Set the parent of the active (or targeted) work item.|ado|
|[`link unparent`](./link-unparent.md)|Remove the parent link from the active (or targeted) work item.|ado|
|[`link reparent`](./link-reparent.md)|Unsupported parent replacement; no mutation.|none|
|[`link predecessor`](./link-predecessor.md)|Mark the active (or targeted) item as blocked by another.|ado|
|[`link successor`](./link-successor.md)|Mark the active (or targeted) item as blocking another.|ado|
|[`link related`](./link-related.md)|Add a symmetric Related edge between two items.|ado|
|[`link unrelate`](./link-unrelate.md)|Remove a Related edge between two items.|ado|
|[`link local related`](./link-local-related.md)|Verify one local-only Related edge with exact Core CAS.|local|
|[`link local unrelate`](./link-local-unrelate.md)|Tombstone a local-only Related edge through the Core journal.|local|
|[`link local list`](./link-local-list.md)|Read local-only Related edges without provider access.|none|
|[`link unlink`](./link-unlink.md)|Remove any non-hierarchy link (`predecessor`, `successor`, `related`).|ado|
|[`link artifact`](./link-artifact.md)|Attach a hyperlink or `vstfs://` artifact link to an item.|ado|

## Shared behavior

- **Published-item targeting.** These verbs operate on the active work item by default. Pass
  `--id <n>` — or, for the hierarchy verbs, the optional second positional
  argument / `--child` — to target a specific item without changing active context.
  Every non-human output requires **`--id`**. A positional child or `--child`
  alone is human convenience, not machine authorization. With `--id`, either
  child form is an optional cross-check and must match. Conflicting child/ID values and
  nonpositive/self targets refuse with usage exit `2` before HTTP. Unpublished
  seeds remain under `seed link`; native links never publish or rewrite local UUIDs.
  Human selection can retain an original seed UUID after publication: the command
  resolves its permanent store mapping to the native item without changing history
  or retained creation proposals. Unmapped/unknown seeds still refuse, and mapped
  endpoints remain subject to the same partial/unknown proposal fences.
- **Local targeting.** Write `link local related <target> --id <source>` or
  `unrelate` with the same shape. Both endpoints must have live Core evidence;
  numeric locators, negative seed aliases and original canonical GUIDv7s are supported.
  The old two-GUID positional form is retired. List requires a positional
  target or `--id`, rejects conflicts, and remains cache-only.
- **Published-item guards.** Self-links are rejected. Adding a link that
  already exists is a refusal (`1`), not a claim of new verification. Removing
  an absent edge is also a failure. One unambiguous discovered canonical shape is
  required; no process/type/native relation name is hardcoded.
- **Published-item verification.** The actual canonical forward source's observed
  revision binds one PATCH: blocker -> blocked for dependencies and parent -> child
  for hierarchy. Native acknowledgement, immutable revision readback and fresh
  reciprocal endpoints must agree. Partial/indeterminate outcomes exit `1` and
  retain durable fences on both endpoints, including under replacement digests.
  No replay, retry or note flush is performed. Local relationships/tombstones
  survive mirror refresh.
- **Published-item output.** `human` and `minimal` render the result and link
  evidence; `json`, `json-full` and `json-compact` emit one parseable document
  with message, count, native links, `status: Verified` and proposal digest.
  `ids` refuses (`2`) because it loses relationship/journal evidence.
  Errors are format-aware on stderr; no success document is emitted on failure.
- **Remaining gaps.** `link reparent` cannot bind replacement to one safe typed
  request. `link related --comment` cannot represent a relation comment. Both
  refuse (`1`) before HTTP, never remove a parent or silently omit authored text.
  Native GitHub CLI relationships, UI cutover and ambiguous native shapes remain
  unqualified. The separate local Related slice requires one discovered local-only
  symmetric Association; ADO declares native Related and refuses local mutation.
- **Local output.** `human`, `json`, `json-full`, `json-compact` and `minimal`
  identify `LOCAL`, `local-only`, `non-native` edges and exact local versions.
  Verified means local CAS, not tracker publication. List alone supports `ids`
  when every neighbor has a unique cached numeric locator or local seed alias.
