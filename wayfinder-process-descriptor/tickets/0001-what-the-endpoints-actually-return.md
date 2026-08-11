---
id: 0001
title: What the process endpoints actually return
type: research
status: closed
claimed_by: starbright-engineering (session 2026-08-10)
blocked_by: []
---

> 🔴 **SUPERSEDED — tracked on the board as [#219](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/219).**
> Do not edit or re-sync this file. Kept for git history only.

## Question

For one inherited process and one stock process, what do the ADO process endpoints actually
return — and which of the absent items are genuinely reachable, at what cost?

This is deliberately first. Every other ticket in this map argues about shape, and shape
arguments made against a guessed payload are worthless.

Specifically, capture real responses for:

- `/_apis/work/processes/{id}/workItemTypes/{refName}/fields` — does it carry `required`,
  `defaultValue`, `allowedValues`, or only references to them? Is a second call needed per
  picklist?
- `/_apis/work/processes/{id}/fields` — 🔴 **reproduce or refute the reporter's `count: 0`
  claim.** It is the one part of GitHub #368 this repo has not verified, and it decides which
  endpoint field enumeration is built on. If it reproduces, record the exact conditions.
- `/_apis/work/processes/lists/{id}` — the report says the list-all endpoint returns empty
  `items` and per-list works. Confirm.
- `/_apis/work/processes/{id}/behaviors` and the per-type behaviors — are these per-process,
  per-type, or both?
- `/_apis/work/processes/{id}/workItemTypes?$expand=...` — what do `customization`,
  `inherits`, and `referenceName` look like on an inherited vs a stock type?

Also measure: how many HTTP calls a full one-type descriptor costs, and a full all-types one.
The reporter's REST run produced 1.09 MB across 15 types; a call count matters as much as the
byte count, because 0004 has to decide whether a human ever waits for this.

## Answer

Full evidence: [`assets/0001-endpoint-findings.md`](../assets/0001-endpoint-findings.md).
Raw payloads under `assets/raw/`, reproducible via `assets/probe-all.py`.

Fixtures: **inherited** = process `Niflheim` (`7f984e4c-…`), which is what the **Twig**
project actually runs on — *not* the process named "Twig", which has zero projects.
**stock** = `Basic` (`b8a3a935-…`, `customizationType: system`). No process needed
creating, so no human gate was hit.

### 🔴 The `count: 0` claim does not reproduce — the endpoint 404s instead

`/_apis/work/processes/{id}/fields?api-version=7.1` returns **HTTP 404**
(`"No HTTP resource was found that matches the request URI"`) for **both** the inherited
and the stock process. `7.1` is simply not a valid api-version on that route.

At a valid version it works and **does carry the custom fields**:

| api-version | inherited | stock |
|---|---|---|
| `7.1` , `7.0`, `6.0`, `7.1-preview.2/.3`, `7.2-preview.2` | **404** | **404** |
| `7.1-preview.1`, `7.2-preview.1`, `6.0/5.1/5.0/4.1-preview.1` | `count: 93`, **13 `Custom.*`** | `count: 80`, 0 custom |

Two other inherited processes agree (85 fields / 5 custom each). The reporter most likely
read the 404's error envelope — which is itself count-shaped,
`{"count":1,"value":{"Message":…}}` — as data.

**Consequence: field enumeration is not forced onto the per-type endpoint by a broken
process-wide one.** Both work. The choice is now a design choice (0002/0003), not a
constraint — and the process-wide list is the cheaper one when a whole-process descriptor
is wanted.

### The bigger finding: api-version changes the SCHEMA, not just the route

On the same per-type URL, `7.1-preview.1` returns `{description,id,isIdentity,isLocked,
name,type,url}` and `7.1-preview.2` returns `{customization,defaultValue,isLocked,name,
referenceName,required,type,url}` — same `count`, disjoint attributes. Surveying at
preview.1 reports `required=True: 0` across all 628 field rows; at preview.2 it reports
**59**. Same split on `/workItemTypes` (`id`/`class` vs `referenceName`/`customization`).
**The descriptor must pin an explicit api-version per route as part of its contract.**

### Point answers

- **`/workItemTypes/{ref}/fields`** — carries `required`, `defaultValue`, `customization`
  at preview.2. Carries **no** `allowedValues` and **no** picklist reference at any
  version, with or without `$expand=all`.
- **`required` from `/fields` alone is wrong.** It reports unconditional requiredness only.
  `Custom.WayfinderAnswer` is `required: null` there while `/rules` carries
  *when State = Done → makeRequired*. The descriptor must merge `/rules` or it lies about
  exactly the fields callers care about.
- **`/processes/lists`** — report **confirmed**: list-all returns every entry with
  `items: []`; per-list returns them. **+1 call per picklist.** Lists are org-wide (the
  stock arm returns the same 7).
- 🔴 **Nothing anywhere associates a picklist with a field.** Checked process `/fields`
  (all versions), per-type `/fields` `$expand=all`, form `layout`, `/_apis/wit/fields/{ref}`
  (`isPicklist: false`, `picklistId: null` at five versions), and project-scoped
  `wit/workitemtypes/{t}/fields/{ref}?$expand=all` (`allowedValues: []`). Only name
  convention links them, and it breaks on the process-prefixed lists. **Ticketed as 0005.**
- **Behaviors are per-process AND per-type.** Process-level `/behaviors` is the catalogue
  (6 inherited vs 5 stock — the extra is the custom backlog level). Per-type is a
  *membership edge* at **`/workItemTypesBehaviors/{ref}/behaviors`** — note
  `/workItemTypes/{ref}/behaviors` 404s for every type. One `$expand=behaviors` on the type
  list gets all of them in a single call.
- **`customization` / `inherits` / `referenceName`** are all free on the type list at
  preview.2 — no extra call. Inherited process: 10 `custom`, 1 `inherited` (`Niflheim.Epic`
  ← `Microsoft.VSTS.WorkItemTypes.Epic`), 3 `system`. Stock: all 6 `system`,
  `inherits: null`.
- **Alternative worth weighing in 0002:** the classic
  `/{project}/_apis/wit/workitemtypes/{name}?$expand=all` returns one type complete
  (fields, fieldInstances, states, transitions, xmlForm) in **1 call, 36 KB**. Also empty
  `allowedValues`. Project-scoped, keyed by display name.

### Cost — measured

Full survey, serial `az rest`, no caching: **inherited 43 calls / 482 KiB / ~19 s**;
**stock 27 calls / 218 KiB / ~8 s**. Dominant cost is **round-trips, not bytes** (~0.45 s
each). Minimum realistic descriptor budget: **one type ≈ 4–6 calls, ~2–3 s**; **all 14
types ≈ 32 calls, ~15 s**. 0004 should treat volume as a *latency* question at least as
much as a byte one; the calls are independent GETs and parallelise trivially.

### Rules volume, for 0002/0003

0 rules on `WayfinderTask`; 1–3 on the authored custom types; **54–55** on the types
derived from system ones, almost entirely inherited system plumbing. Dumping rules
verbatim is ~95% noise. `customizationType` per rule is the available filter.
