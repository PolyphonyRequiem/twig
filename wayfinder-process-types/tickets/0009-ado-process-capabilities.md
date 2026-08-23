---
id: 0009
title: What can an ADO inherited process actually express for types, fields, gates and hidden types?
type: research
status: closed
claimed_by: research-0009
blocked_by: []
---

## Question

For an **inherited** ADO process, what is actually permitted — and what is refused — in each of
these areas? Primary sources plus live probes against the Sandbox, with citations.

1. **Creating a work item type.** Required inputs; whether it can be created from scratch or
   only `inherits` a system type; what `inherits` fixes forever; whether a type can be created
   with no backlog level; what colour/icon accept.
2. **Assigning a type to a backlog level.** The exact PUT (⚠️ **behaviour edits are PUT; PATCH
   returns 405**); whether a type can sit at two levels; whether a level can be removed later.
3. **Renaming and removing types.** Can a custom type be deleted, or only disabled/hidden? Can
   an **inherited-but-unused** type (`Code Review Request`/`Response`, `Feedback Request`/
   `Response`) be hidden or removed — this is the direct input ticket 0005 needs. Does hiding
   stop a REST write, or only hide it in the UI? **Measure a write against a hidden type.**
4. **Fields.** Creating a custom field; picklists and `allowedValues` on a `string`; making a
   field required; **making a field required only on a specific state transition** (this is what
   the Bug→Done gate is, so establish the mechanism precisely); removing a field from one type
   without deleting it globally; what happens to existing data.
5. **Rules.** What a process rule can condition on and what it can do; whether a rule can
   enforce a gate that a REST client cannot bypass.
6. **States.** Adding a state; the state *category* (`Proposed`/`InProgress`/`Completed`) and
   what it governs; whether a custom state can be added to an inherited type.

## Why this exists

Tickets 0005, 0007 and 0008 can each design something ADO cannot express. This ticket makes them
answerable rather than speculative, and it is **unblocked** so it can run in parallel with the
early grilling tickets.

## Already answered — do not redo

These are settled with primary sources in
`/home/polyphonyrequiem/repos/twig-bench-unify/wayfinder-bench-unify/`
(`ado-backlog-levels.md`, `ado-parent-child-enforcement.md`, `ado-process-inheritance.md` +
`-probe.md`, `ado-audience-views.md`):

- **ADO cannot enforce type-level parent/child policy at all** — six avenues closed. Hence
  ADO #615.
- **Backlog level governs display, not link legality.**
- **Multi-level process inheritance does not exist** — proven live with a control: a custom
  parent returns `HTTP 500 VS402372` while a byte-identical request with a system parent returns
  `201`. One process, many teams.

Read them first. Cite them rather than re-probing.

## How to probe

**`PolyphonyRequiem/Sandbox` carries the same Hyperbright process and is safe to write. Never
probe process mutations against the live `Twig` board.**

```bash
export HOME=/home/polyphonyrequiem
mkdir -p ~/scratch/process-types && cd ~/scratch/process-types
twig init --org PolyphonyRequiem --project Sandbox
```

```bash
TOK=$(az account get-access-token --scope 499b84ac-1321-427f-aa17-267ca6975798/.default \
      --query accessToken -o tsv)
PID=ba4e268d-7d67-43bd-8065-df7ab52fba0c
curl -s -H "Authorization: Bearer $TOK" \
  "https://dev.azure.com/PolyphonyRequiem/_apis/work/processes/$PID/workitemtypes?\$expand=behaviors&api-version=7.1-preview.2"
```

🔴 **Never trust a write's own success message — re-read with an independent GET.** This board
has produced a `copyFrom` probe returning **`201 Created` that silently did nothing**, a
`twig seed publish --all` reporting "no seeds" with six staged, and a research agent reporting
`completed` having written no file. A probe without a verifying GET is not evidence.

⚠️ Read the real `referenceName` off `GET .../workitemtypes` before querying a type. Assuming
`Hyperbright.<Name>` for a stock type returns `VS402805: Cannot find work item type` — custom
and inherited types use the `Hyperbright.` form, the test types use
`Microsoft.VSTS.WorkItemTypes.*`.

## Answer

**Resolved 2026-08-22.** The memo is `../ado-process-capabilities.md` (552 lines), primary-source,
every claim marked **documented** (with a learn.microsoft.com URL) or **probed live** (with the
exact request and its independent verifying GET). Probes ran against `PolyphonyRequiem/Sandbox`;
everything created was cleaned up. All six question areas are answered; §8 lists the residual
gaps honestly rather than padding them.

The findings that change other tickets — the rest is in the memo, which is the artifact:

1. **Type creation** — a type can be created from scratch with **no parent and no backlog
   level**. `inheritsFrom` a *custom* type is refused (`404 VS402805`), the type-level mirror of
   the settled no-multi-level-inheritance finding. Colour is bare 6-hex; icon is a closed enum.
2. **Backlog level** — the endpoint is `workitemtypesbehaviors/{rn}/behaviors`, POST to add and
   DELETE to remove. 🔴 **A type cannot sit at two levels** — hard `400 VS403194`.
   ⚠️ **Correction to the brief:** PATCH on this route returns **500**, not 405.
3. **The four hidden Request/Response types cannot be hidden further or removed, because they are
   not process objects at all.** Absent from the 16-type process roster; individual GET →
   `VS1640142`; PATCH disable → `VS402805`. They exist only at *project* scope in
   `Microsoft.HiddenCategory`, which is a **UI-only** hide — a real `Code Review Request` was
   created via REST (id 655, HTTP 200, destroyed after). By contrast `isDisabled` on a
   `Hyperbright.*` type **does** block REST creates (`VS403074`).
   ⚠️ **This finding is accurate but was gathered to inform a question that turned out not to
   exist.** The ticket asked it because the brief framed the four as a naming collision for
   `Request for Change`; the author corrected that on 2026-08-22 — they are **tooling back ends,
   not namable vocabulary**, never offered to a chooser. Ticket 0005 is rescoped accordingly.
   Related, already filed and **not this map's work**: **ADO #656**, **ADO #657**.
4. **Fields** — field removal is per-type and leaves the org field and other types intact.
   ⚠️ **Correction to the brief and to this map's first draft:** `Custom.VerificationMode`
   **does** have enforced `allowedValues` — a five-item picklist. The *process* API returns a
   stub, which is what produced the "free text" reading; the **project WIT** endpoint with
   `$expand=all` shows the values.
5. **The gate mechanism, pinned** — two custom rules, `conditionType: "when"` on `System.State`
   = `Done`, `actionType: "makeRequired"`. A **state** rule, not a transition restriction; every
   transition is legal. 🔴 **The most consequential finding on this ticket:
   `bypassRules=true` closed a Bug with both gate fields empty — HTTP 200, verified by GET.**
   Process rules are advisory against a privileged identity. **Type-disabling is the only
   mechanism found that `bypassRules` cannot walk through.**
6. **States** — a custom state can be added to an **inherited** type (probed on Task), sitting
   alongside `system`-marked originals; deleted and verified back to three. Two states may share
   one category.

**Left OPEN, stated as such:** renaming a custom type was not probed (§3.6). The memo says what
is known — `referenceName` is fixed at creation and never tracks a later rename — and does not
guess at the rest.

**Verification of the verifier.** The two most consequential claims — the picklist and the
dormant types' absence from the process — were **independently re-checked against the API by the
parent session**, not taken on the subagent's report, per this repo's rule that a write's own
success message is not evidence. Both held.

## Consequences already applied

- `map.md` — the "free text `VerificationMode`" claim corrected, and the gate recorded as
  advisory-not-inviolable.
- Ticket 0005 — **rescoped 2026-08-22 by an author correction, not by this ticket.** The
  collision premise was a factual error: the four are `Microsoft.HiddenCategory` tooling back
  ends, never offered to a chooser, so `Request for Change` is judged on its own merits. §3.5's
  measurement stands but is no longer load-bearing.
- Ticket 0007 — the picklist sub-question marked largely answered, and a new sub-question added:
  what a gate is worth given it is bypassable.

## Do not

- Do not re-probe what the memo marks **probed live**; it carries the verifying GET.
- Do not treat §3.6 (rename) as answered.

