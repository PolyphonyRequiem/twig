# 0001 — Captured ADO process endpoint payloads

Raw payloads captured live against **org `PolyphonyRequiem`** on **2026-08-11**.

| Arm | Process | Type id | Types |
|---|---|---|---|
| Inherited | **Niflheim** (parent Basic) — the process the **Twig** project actually runs on | `7f984e4c-e856-4fc3-8457-fd4e8acf2e57` | 14 (11 custom/derived) |
| Stock | **Basic** (`customizationType: system`) | `b8a3a935-7e91-48b8-a94c-606d37c3e9f2` | 6 |

> 🔴 The **Twig project does not run on the process named "Twig"**. `?$expand=projects`
> shows `Niflheim` owns `Hyperbright`, `Twig`, `Niflheim`; the processes named `Twig` and
> `Hyperbright` have **zero** projects. Anything resolving a descriptor by process *name*
> will silently describe the wrong process.

## Reproduce

```bash
export AZURE_CONFIG_DIR=/home/<login-user>/.azure     # NOT the Hermes profile home
cd wayfinder-process-descriptor/assets
python3 probe-all.py > survey.txt        # ~30 s, writes raw/*.json
./probe.sh <name> '<path-after-org>'     # one-off probe
```

## 🔴 The headline: `{"count":0}` does NOT reproduce — the endpoint 404s instead

The reporter's exact URL is not a *thin* response, it is **not a response at all**:

```
GET /_apis/work/processes/{id}/fields?api-version=7.1
→ 404  {"Message":"No HTTP resource was found that matches the request URI ..."}
```

**`7.1` is not a valid api-version for this route.** Working versions, all returning the
same body byte-for-byte (32 289 bytes):

| api-version | inherited (Niflheim) | stock (Basic) |
|---|---|---|
| `7.1` | **404** | **404** |
| `7.1-preview.1` | `count: 93`, 13 `Custom.*` | `count: 80`, 0 custom |
| `7.2-preview.1` | `count: 93` | — |
| `6.0-preview.1` / `5.1-preview.1` / `5.0-preview.1` / `4.1-preview.1` | `count: 93` | — |
| `7.1-preview.2` / `7.1-preview.3` / `7.0` / `6.0` / `7.2-preview.2` | 404 | 404 |

Other inherited processes agree: `Twig` proc = 85/5 custom, `Godsblood` = 85/5 custom.

So the process-wide `/fields` endpoint **works and does carry the custom fields.** The
reporter almost certainly hit the 404 and read the error envelope — which is itself
`{"count":1,"value":{"Message":...}}`, a *count-shaped* body — as a data response.

## The real trap: api-version silently changes the SCHEMA, not just the route

This is the finding that matters more than the `count:0` claim. On the *same* URL, two
preview versions return **disjoint attribute sets**:

`/_apis/work/processes/{id}/workItemTypes/{ref}/fields`

| version | keys returned |
|---|---|
| `7.1-preview.1` | `description, id, isIdentity, isLocked, name, type, url` |
| `7.1-preview.2` (also `7.2-preview.2`, `6.0-preview.2`) | `customization, defaultValue, isLocked, name, referenceName, required, type, url` |

Both return `count: 45` for `Niflheim.Grilling`. **preview.1 has no `required` and no
`defaultValue` at all.** Enumerating at preview.1 and concluding "the API doesn't expose
required" is a live failure mode; the whole-process survey at preview.1 reported
`required=True: 0` across all 628 field rows, and the identical survey at preview.2
reported **59**.

Same split on `/workItemTypes`: preview.1 returns `id` + `class`, preview.2 returns
`referenceName` + `customization`. The reporter's `customization`/`inherits` fields are
preview.2 vocabulary.

**Consequence for the descriptor: pin an explicit api-version per route and treat it as
part of the contract.** `7.1` is not universally valid; `7.1-preview.2` is the richest
shape for fields and types, `7.1-preview.1` for process-level `/fields` and picklists.

## Per-endpoint results

### `/workItemTypes/{ref}/fields` @ `7.1-preview.2` — the field-enumeration answer

Carries `required`, `defaultValue`, `customization`. **Does not carry `allowedValues` or
any picklist reference at any version, with or without `$expand=all`.** Example
(`Niflheim.Grilling`):

```json
{"customization":"custom","description":"","isLocked":false,"name":"Execution Mode",
 "referenceName":"Custom.WayfinderExecutionMode","type":"string", "url":"…/behaviors"}
```

(`url` on custom rows is wrong — it points at `…/behaviors`. Cosmetic, but do not build a
follow-up fetch on it.)

Inherited process, 628 field rows over 14 types: **59 required, 19 with a defaultValue,
0 with allowedValues, 0 with pickList.** Stock: 260 rows, 27 required, 11 defaults.

🔴 Every `Custom.*` field on every custom type reads `required: None` here. That flag
reports only **unconditional** requiredness. Conditional requiredness lives in
`/workItemTypes/{ref}/rules` and is invisible to this endpoint — e.g. `Niflheim.Grilling`
has a rule *"Grilling must record its answer"*:

```json
{"conditions":[{"conditionType":"when","field":"System.State","value":"Done"}],
 "actions":[{"actionType":"makeRequired","targetField":"Custom.WayfinderAnswer"}]}
```

`Custom.WayfinderAnswer` is `required: None` on `/fields` and *required-at-Done* in
`/rules`. **A descriptor that reports `required` from `/fields` alone will be wrong about
the fields a caller most needs**, and wrong in the silent direction. It must merge
`/rules`. (This is the same class as the standing `TF401320 on close = required-on-close`
trap, now confirmed as a descriptor-shape problem, not just an operator one.)

#### `/workItemTypes/{ref}/rules` @ `7.1-preview.2` — volume is wildly uneven

| type | rules |
|---|---|
| `Niflheim.WayfinderTask` | 0 |
| `Grilling` / `Research` / `Prototype` / `Map` | 1 |
| `Idea` / `Spec` | 2 |
| `Decision` | 3 |
| `Niflheim.Task` / `Epic` | **54** |
| `Niflheim.Issue` | **55** |

The custom types carry 0–3 authored rules; the types derived from system ones carry ~54,
almost all inherited system plumbing (`copyFromServerClock` on `ActivatedDate`,
`makeReadOnly` on `CreatedBy`, the `System.Reason` transition matrix). 🔴 **A descriptor
that dumps rules verbatim is ~95% noise on exactly the types a caller is most likely to
ask about.** `customizationType` on each rule (`custom` vs inherited) is the available
filter — 0002/0003 should decide whether the descriptor reports authored rules, all rules,
or a derived `required-when` projection onto fields.

### Picklists — resolvable, but **not linked from anywhere**

- `/_apis/work/processes/lists` (list-all): `count: 7`, every entry `items: []` — the
  report's claim **confirmed**. Metadata only (`id, name, type, isSuggested`).
- `/_apis/work/processes/lists/{id}`: full items. **1 extra call per picklist.**
- Picklists are org-wide, not per-process: the *stock* Basic arm returns the same 7 lists.

🔴 **Nothing on any process endpoint names which picklist backs which field.** Checked and
came up empty: process `/fields` (all versions), per-type `/fields` (`$expand=all`), the
form `layout`, `/_apis/wit/fields/{ref}` (`isPicklist: false`, `picklistId: null` at 7.1,
preview.2, preview.3, 7.2-preview.3, 6.0), and the project-scoped
`/{proj}/_apis/wit/workitemtypes/{t}/fields/{ref}?$expand=all` (`allowedValues: []`).

The only available association is **name convention** — field `Custom.WayfinderExecutionMode`
↔ list `WayfinderExecutionMode`, `Custom.Maturity` ↔ `NiflheimMaturity`. That is a
heuristic, not a contract, and it breaks on the process-prefixed ones.

**This is a genuine, verified capability gap and it deserves its own ticket** — 0002/0003
cannot promise resolved picklist values in the descriptor until it is answered.

### Behaviors — per-process AND per-type, at different routes

- `/_apis/work/processes/{id}/behaviors?api-version=7.1-preview.2` — process-level.
  Inherited: 6 (`Wayfinding, Epics, Issues, Ordered, Tasks, Portfolio`). Stock: 5. The
  extra one is the custom backlog level.
- 🔴 Per-type is **`/workItemTypesBehaviors/{ref}/behaviors`**, not
  `/workItemTypes/{ref}/behaviors` — the latter 404s for every type on both arms
  (an HTML 404 page, not a JSON envelope). Works at `7.1-preview.1` / `6.0-preview.1`.
  Returns a reference only: `{"behavior":{"id":"Custom.3daa…","url":…},"isDefault":false}`
  — 1 further call to name it.
- `?$expand=behaviors` on the type list gets all of them in **one** call (10 889 chars).

So behaviors are **both**, and the answer to the map's fog is: process-level is the
*catalogue* of backlog levels; per-type is the *membership* edge. A per-type descriptor
needs the membership; only a whole-process one needs the catalogue.

### Type metadata

Inherited process, `?api-version=7.1-preview.2`:

| type | class (p.1) | customization (p.2) | inherits |
|---|---|---|---|
| `Niflheim.Grilling` … `Niflheim.WayfinderTask` (10) | `custom` | `custom` | `null` |
| `Niflheim.Epic` | `derived` | `inherited` | `Microsoft.VSTS.WorkItemTypes.Epic` |
| `Microsoft.VSTS.WorkItemTypes.TestCase/Plan/Suite` | `system` | `system` | `null` |

Stock Basic: all 6 `system` / `system`, `inherits: null`. `referenceName` is free on the
type list — no extra call, contrary to the map's "resolved transiently then discarded".

### The classic `wit` API is a viable alternative worth noting

`/{project}/_apis/wit/workitemtypes/{name}?$expand=all` returns **one type complete** —
`fields`, `fieldInstances`, `states`, `transitions`, `xmlForm` — in **1 call, 36 KB**. It
carries `alwaysRequired` and `defaultValue` but, again, empty `allowedValues`. It is
project-scoped and speaks type *names* not reference names. Not obviously worse than the
process API for a per-type descriptor, and much cheaper. 0002 should weigh it.

## Cost — measured, not estimated

Full survey of every endpoint above, `az rest` serial, no caching:

| arm | HTTP calls | bytes | wall |
|---|---|---|---|
| inherited, 14 types | **43** | 482 KiB | ~19 s |
| stock, 6 types | **27** | 218 KiB | ~8 s |

The dominant cost is **calls, not bytes** — ~0.45 s each, so the inherited arm is ~19 s of
almost pure round-trip. The reporter's 1.09 MB over 15 types is the right order of
magnitude (this survey pulls layout twice and probes dead versions).

Minimum realistic budget:

- **one type**: 3 calls (type list `$expand=behaviors`, per-type `/fields`, per-type
  `/rules`) + 1 per referenced picklist ≈ **4–6 calls, ~2–3 s**.
- **all types**: 1 + 2×N + picklists ≈ **~32 calls, ~15 s** for 14 types.

🔴 **A human will notice.** 15 s for the all-types case is not "fast enough to ignore" —
0004's volume question should be read as a *latency* question at least as much as a byte
one, and the calls parallelise trivially (they are independent GETs).

## Answers to the map's open fog

- **Behaviors are per-process AND per-type**, at two different routes (above). A per-type
  descriptor needs the membership edge; only a whole-process one needs the catalogue.
  Cost is 1 call either way via `$expand=behaviors`, so this is a *shape* decision, not a
  cost one. Ready to graduate out of "Not yet specified".
- **Volume (0004)**: measured above. The binding constraint is round-trips, not bytes.

## New facts 0002/0003/0004 have to design against

1. Pin an explicit api-version **per route**; `7.1` 404s on the process-field route.
2. `required` needs `/fields` **merged with** `/rules`, or it lies.
3. Rules are 0–3 on authored types and ~55 on derived ones — filter or project, don't dump.
4. Picklist → field association is **not exposed by any endpoint**. Ticketable gap.
5. Resolve the process by **id via the project**, never by process name.

## Files

`raw/` holds every payload. Notable:

- `raw/inherited-process-fields.json` — the disputed endpoint, working, 93 fields
- `raw/Niflheim-fields-Niflheim_Grilling.json` — rich per-type shape (preview.2)
- `raw/tf-7.1-preview.1.json` vs `raw/tf-7.1-preview.2.json` — the schema split
- `raw/Niflheim-lists-all.json` / `raw/Niflheim-list-*.json` — empty vs populated items
- `raw/wit-type-expand.json` — the classic-API one-call alternative
- `survey.txt` — full run output
