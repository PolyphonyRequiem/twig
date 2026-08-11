---
id: 0005
title: Can a picklist be associated with its field at all
type: research
status: open
claimed_by:
blocked_by: []
---

> 🔴 **SUPERSEDED — tracked on the board as [#223](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/223).**
> Do not edit or re-sync this file. Kept for git history only.

## Question

0001 established that **no ADO endpoint associates a picklist with the field it backs.**
Verified empty across: process `/fields` (every working api-version), per-type `/fields`
with and without `$expand=all`, the form `layout`, `/_apis/wit/fields/{ref}`
(`isPicklist: false`, `picklistId: null` at 7.1, 7.1-preview.2, 7.1-preview.3,
7.2-preview.3, 6.0), and project-scoped
`/{project}/_apis/wit/workitemtypes/{type}/fields/{ref}?$expand=all` (`allowedValues: []`).

The only link is **name convention** — `Custom.WayfinderExecutionMode` ↔ list
`WayfinderExecutionMode` — and it already breaks in this org: `Custom.Maturity` ↔
`NiflheimMaturity`, `Custom.PriorityBand` ↔ `NiflheimPriorityBand`.

**This ticket decides whether resolved picklist values can be in the descriptor at all.**
0002 and 0003 must not promise them until it closes.

Specifically:

- Is there any endpoint, api-version, or `$expand` not yet tried that carries the link?
  Candidates not exhausted: the process **export/import** payload (`/_apis/work/processes/{id}`
  with export expansions), the `xmlForm` on the classic type (checked once, no
  `ALLOWEDVALUES`, but only on one type), and the WIT **field usage / picklist** admin routes.
- If not, is the association **only** recoverable by writing a picklist and observing the
  field, i.e. only known to whoever created it?
- If it is genuinely unavailable, what is honest to emit? Options: omit picklists entirely;
  emit the org's picklists as a separate unlinked section; emit a
  best-effort name-convention guess **explicitly labelled as a guess**; or emit the list of
  values a field has actually been *observed* to hold.

🔴 Do not settle this with a name-matching heuristic quietly. A descriptor that silently
guesses which values a field accepts is worse than one that says it does not know — that
is the same failure class as the current project-wide field list, which is untrue about
which fields belong to the type.

## Answer

<!-- empty until resolved -->
