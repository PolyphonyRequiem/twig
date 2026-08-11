# Ticket 0005 — can a picklist be associated with its field at all?

**Answer: yes, on two routes. And the premise that motivated the ticket was wrong.**

Ran live against org `PolyphonyRequiem`, 2026-08-11. All probes read-only except one
create/delete experiment on the **`Twig` process, which has zero projects** (see Cleanup).

```bash
export AZURE_CONFIG_DIR=/home/<login-user>/.azure     # NOT the Hermes profile home
```

## 🔴 Finding 1 — the association is carried, on two routes

0001 concluded no endpoint links a picklist to its field. That is **true of every route it
checked**, and false of the API as a whole. Two routes carry it:

### `/_apis/wit/fields/{ref}` — `picklistId`, org scope

```json
{ "referenceName": "Custom.Probe0005Choice",
  "isPicklist": true, "isPicklistSuggested": false,
  "picklistId": "4f7202b2-4d64-409c-8697-ab3db58b5ca3" }
```

`picklistId` is a **conditional key**: it is *absent* (not null) when `isPicklist` is false.
0001 read `isPicklist: false, picklistId: null` and concluded the endpoint does not carry the
link. It does — those fields simply are not picklist-backed (Finding 2).

### `/_apis/work/processdefinitions/{p}/workItemTypes/{t}/fields` — `pickList`, type scope

The legacy `processdefinitions` route (`api-version=4.1-preview.1`), never probed by 0001,
returns a **`pickList` object inline, with the name** — no second call needed for identity:

```json
{ "referenceName": "Custom.Probe0005Choice", "type": "string",
  "required": false, "defaultValue": null, "allowGroups": null,
  "pickList": { "id": "4f7202b2-…", "name": "Probe0005List",
                "type": "String", "isSuggested": false } }
```

This route is the better one for a descriptor: it is **type-scoped** (so it also answers
"which fields belong to this type", the map's load-bearing defect), and it carries
`required` and `defaultValue` in the same payload. `pickList` is `null` for non-picklist
fields — an explicit negative, not an absent key.

Its limits: `api-version=4.1-preview.1` only, no `{ref}`-scoped GET on the *process*-level
`/fields` (405 Method Not Allowed), and it is a legacy surface Microsoft does not document
alongside the `processes` API. Pinning it is a real contract decision for 0003.

## 🔴 Finding 2 — the org's seven picklists are orphans; none backs any field

The name-convention worry (`Custom.WayfinderExecutionMode` ↔ list `WayfinderExecutionMode`)
is not a weak link — **there is no link at all**. Checked every custom field in Niflheim:

| field | `isPicklist` | `picklistId` |
|---|---|---|
| `Custom.WayfinderExecutionMode` | false | absent |
| `Custom.WayfinderDecisionMaturity` | false | absent |
| `Custom.Maturity` | false | absent |
| `Custom.PriorityBand` | false | absent |
| `Custom.VerificationMode` | false | absent |
| `Custom.DecisionStanding` | false | absent |
| `Custom.IdeaOutcome` | false | absent |

Org-wide: `/_apis/wit/fields` returns 199 fields, **zero** with `isPicklist: true`. The seven
lists exist and hold values; nothing consumes them.

**Independently corroborated by the server's own validator.** A `validateOnly=true` PATCH
writing `"ZZZ_NOT_A_VALUE"` into `Custom.WayfinderExecutionMode` on work item #223 was
**accepted**. The control — the same probe against `System.State` — was rejected:

```
The field 'State' contains the value 'ZZZ_BOGUS' that is not in the list of supported values
fieldStatusFlags: "required, hasValues, limitedToValues, invalidListValue"
```

So ADO itself does not constrain those fields. They are plain strings. The board's
apparent enum behaviour is convention held by whoever writes to it, not the server.
🔴 **This is a live defect in the Niflheim process, not a twig defect** — and it means any
descriptor that reported those fields as enums would be *lying*, in exactly the way the
project-wide field list already lies.

## Finding 3 — routes that genuinely do not carry it (0001 confirmed, plus new)

| route | result |
|---|---|
| process `/fields` (all working versions) | no picklist reference (0001) |
| per-type `/fields` ± `$expand=all` | no picklist reference (0001, re-confirmed) |
| `/_apis/work/processes/{p}/workItemTypes/{t}/fields/{ref}` @preview.2 | 7 keys, none picklist |
| form `layout` | controls carry `controlType` only; no allowed values |
| project `/_apis/wit/workitemtypes/{t}?$expand=all` | `allowedValues: []`, `xmlForm` has **0** `ALLOWEDVALUES` across Grilling/Research/Idea/Decision/Map — the single-type check in 0001 generalises |
| `/_apis/wit/fields?$expand=extensionFields` | same shape as unexpanded |
| `/_apis/work/processadmin/processes/export/{id}` | **feature disabled** on this org (`FeatureDisabledException`) |
| `/_apis/Contribution/HierarchyQuery` (4 process data providers) | all returned empty — provider ids do not resolve |
| `/_apis/work/processes/lists/{id}/fields` (reverse lookup) | 404, no such controller |

There is **no reverse route**: given a picklist you cannot ask which fields use it. The
association is only readable field-first.

## The experiment that settled it

Because no existing field was picklist-backed, absence of the link could not be
distinguished from absence of the *capability*. So one was created and observed:

1. `POST /_apis/work/processes/lists` → list `Probe0005List` = [Alpha, Beta, Gamma].
2. `POST /_apis/wit/fields` with `isPicklist: true, picklistId: <id>` → field
   `Custom.Probe0005Choice`. 🔴 This is the **only** way to bind them: the
   `processes` and `processdefinitions` field-creation routes both reject the attempt
   (`ArgumentNullException: fieldReferenceName` / `VssPropertyValidationException:
   ReferenceName`) — binding happens at **org field creation**, not at type attachment.
3. `POST /_apis/work/processes/{p}/workItemTypes/Twig.Research/fields` to attach it.
4. Read it back on every route → the two disclosures above.

**Consequence for the "only knowable to whoever created it" question in the ticket: no.**
The association is durably queryable after the fact by anyone with read access. It is
*write*-restricted to field creation, not *read*-restricted.

## Cleanup

Run on the **`Twig` process**, which has `projects: []` — deliberately not Niflheim, which
backs three live projects. All three artifacts deleted; verified after: `/_apis/wit/fields`
back to 199 with zero `isPicklist: true`, `/_apis/work/processes/lists` back to the original
seven. No residue.
