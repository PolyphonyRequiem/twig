---
id: 0009
title: What can an ADO inherited process actually express for types, fields, gates and dormant types?
type: research
status: open
claimed_by:
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

## Output

A memo in this map's directory (`ado-process-capabilities.md`), primary-source with citations,
each claim marked **documented** or **probed live** — and for probed claims, the verifying GET.
State refusals as explicitly as permissions: a "cannot" that later turns out to be "did not try
the right call" is worse than an open question.
