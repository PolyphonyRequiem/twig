---
id: 0005
title: Define optional editing capabilities without mandatory persistence
type: grilling
status: closed
claimed_by: edit-capability
blocked_by: [0002, 0003]
---

## Question

What explicit capability contract lets an editable host discover allowed edits, validate or propose changes, and hand mutations to a caller-owned sink without requiring `IPendingChangeStore` or Twig.Infrastructure for read-only use?

Resolve capability discovery, field mutability, validation/state transitions, change representation, optimistic concurrency/error reporting, and ownership of persistence. Do not make the projection itself mutable or let a null persistence service become an implicit mode switch.

## Answer

**Settled as a design contract, ratified by Daniel 2026-08-09.** Pinned at commit
`76572ea67916bed3971d7b72e86bac234bc928b3` (`origin/main`, "feat(tui): migrate the work
item form onto the shared detail projection AB#155"), working tree clean except this
ticket and the map. Every current-code claim below was read at that commit. No production
code was written — this is a `type: grilling` ticket and its deliverable is the contract.

The governing rule, and the one sentence to carry forward:

> **Capability is a second thing you deliberately acquire. The document never changes shape,
> and the destination that will store a change is the authority on what may be changed.**

### 1. Field mutability — the sink declares it, not the server

**The caller-owned persistence sink declares which field reference names it can persist, and
exactly those controls accept input.** `DetailControl.ReadOnly` remains **reported, never
enforced** — 0002 §6 and 0002 §11 survive this ticket unchanged, and 0004's
`LoadDocument_ServerReadOnlyIsNotEnforced_OnlyReported` stays valid.

🔴 **The trap 0004 laid was declined consciously, and here is the argument.** Wiring
editability to `ReadOnly` looks like the safe move. It is the opposite. ADO marks almost no
field read-only, so `ReadOnly`-as-authority makes nearly the whole form typable while Twig
can persist three fields. The user types into Priority, saves, and the edit is silently
discarded — the sink had nowhere to put it. That is the worst available failure: the UI
invited the edit, then ate it.

Sink-declared mutability cannot produce that failure by construction, because the editable
set **is** the persistable set. The inverse failure — a field locked that the server would
have accepted — is visible, honest, and fixed by teaching the sink one more field.

A third option (ANDing sink capability with `ReadOnly`) was considered and rejected. It does
not cause silent loss, but it converts the reporting flag into a veto sourced from a
per-process form definition that can be stale or wrong, producing fields the user cannot edit
for a reason no host can explain.

**Consequence for Twig's own TUI:** `WorkItemFormView.EditableFieldRefs` stops being a
hard-coded list and becomes a *consequence* of what `IPendingChangeStore` declares. Same
three fields (`System.Title`, `System.State`, `System.AssignedTo`) today, different and
correct reason.

### 2. Capability discovery — a separate object, not a stamped field

**The document is unchanged. An editable host acquires a separate capability object and
correlates it to controls by field reference name.** A read-only host never acquires it and
never learns it exists.

Two rejected alternatives, with reasons:

- **Stamp `Editable` onto each `DetailControl`.** Convenient to render, but the projection
  would need to know about the sink to build the document, so `Project` stops being a pure
  function of `FormLayout` + `WorkItemSnapshot` and the read-only path starts paying for the
  editing path. 0003 proved a read-only host needs no store; this would undo it.
- **No discovery — offer everything, refuse at save.** Reintroduces §1's silent-loss failure
  one layer later.

This satisfies both of the ticket's hard constraints simultaneously: the projection stays a
value bundle, and capability is explicitly acquired rather than switched on by a null
argument. **There is no "pass null and it becomes read-only" mode.** A host either has a
capability object or has never asked for one.

The honest cost: an editable host holds two things and joins them by field reference name.

### 3. State transitions — offer-time filtering AND entry-time validation, with the server final

**Both.** The capability object answers "which states may this item move to right now?" so a
host can offer only legal targets, **and** re-validates on the way in so a host that ignores
the offer list cannot push an illegal transition into the sink.

The material already exists in `Twig.Domain` and needs no infrastructure:
`ProcessConfiguration.GetTransitionKind` and `StateTransitionService.Evaluate` are a pure
evaluation over the process config.

🔴 **Both checks are advisory, not authoritative, and the contract must say so.**
`StateTransitionExecutor`'s own remarks record why: *"ADO's per-process transition graph
requires process-admin permissions to fetch. Since most twig users are contributors (not
admins) we cannot pre-cache the graph and BFS for a shortest path."* Twig's legal-transition
set is therefore inferred from standard process templates (Agile, CMMI, Scrum, Basic), not
read from the team's actual workflow. ADO can refuse a transition Twig believed was legal.

A host that treats the offered list as truth will render a legitimate server refusal as a
bug. The contract carries three layers, in order: **offer-time filter → entry-time validation
→ server is final.**

### 4. Change representation — a state move is its own kind, and may carry field changes

**A proposed state transition is a distinct change kind, not a field write named
`System.State`.** Field changes may accompany it.

Checked against the ADO wire path before banking, per Daniel's provisional agreement:

- 🔴 **The wire does NOT justify this.** ADO takes one JSON-patch of field values and
  `System.State` sits in it like any other field. Recording this explicitly so nobody later
  "simplifies" the contract back to a uniform field list on the grounds that the server does
  not distinguish them.
- **The multi-hop behaviour does justify it, and it is real and already built.**
  `StateTransitionExecutor.ExecuteAsync` attempts the direct PATCH, and on a transition
  rejection walks `TypeConfig.States` through intermediates, one PATCH per hop, returning the
  traversed path. One user-visible state move becomes N writes. No ordinary field change ever
  does this.
- **Twig's existing vocabulary already splits them.** `IPendingChangeStore` distinguishes
  `ChangeType` `"state"` from `"field"`; `IMutationProvider` has separate `ChangeStateAsync`
  and `UpdateFieldAsync`; `StateTransitionOutcome` is a discriminated union with cases
  (`TransitionNotAllowed`, `ChainFailed` carrying the partial path) that have no field-write
  analogue.
- **Accompanying field changes are already supported**, via
  `ExecuteAsync`'s `additionalChangesFactory`. So "a state move, plus these field changes" is
  the natural unit of work, not two unrelated errands.

Cost accepted: a sink indifferent to states still handles two shapes.

### 5. Change representation — carry the prior value

**Each proposed change carries both the value the user started from and the new value**,
matching the existing `FieldChange(FieldName, OldValue, NewValue)` and
`AddChangesBatchAsync`'s `(ChangeType, FieldName, OldValue, NewValue)` tuple.

🔴 **The prior value is NOT the concurrency check.** Concurrency is revision-based —
`WorkItem.Revision`, `MarkSynced(int)`, and an `expectedRevision` passed to the PATCH. The
contract must not imply last-write-wins-by-value comparison, because that is a different and
weaker guarantee than what is actually implemented.

The prior value exists for: rendering *what changed* rather than what it now is (the
duplicate-review pane's whole purpose), confirm-before-save, and undo without a re-fetch. It
is also a fact Twig already holds — `WorkItemFormView` keeps per-row originals — so dropping
it discards information that cannot be cheaply recovered later.

Cost accepted: a long description carries its full prior text.

### 6. Optimistic concurrency — retry-and-report by default, resolvable by choice

**Default: retry once against the refreshed revision, then report the collision. The report
carries the remote values**, so a host that wants to resolve it can, without inventing its
own machinery.

Today's behaviour is the floor, not the ceiling: `AdoMutationProvider` refetches, retries via
`ConflictRetryHelper.PatchWithRetryAsync`, and on `AdoConflictException` returns
`MutationResult.Error("Concurrency conflict after retry. Run 'twig sync' and retry.")` — a
string. Adequate for a CLI, thin for a pane holding unsaved user input.

The two rejected alternatives: **capability decides and reports only** (right floor, wrong
ceiling — a review pane can show both versions side by side and a terminal form usually
cannot, so the information must be available), and **capability never decides** (wrong floor
— forcing every host to implement conflict resolution before it can save anything at all).

Cost accepted, and it is the largest specification cost in this ticket: "enough detail to
resolve" means the failure carries the remote revision's values, not a message string.

### 7. Ownership of persistence — the host supplies the sink

**The host supplies the sink. Twig ships one — `IPendingChangeStore` — and it is not
privileged.**

The alternative (Twig always owns the destination) would drag Twig's SQLite store into any
pane that wants to edit, defeating precisely what 0003 proved: `samples/Twig.DetailHost`
runs with zero transitive runtime packages and no `Twig.Infrastructure`. It also mismatches
the first real customer — Bonsai's duplicate-review pane wants decisions in a review queue,
not in a work-item staging table.

This closes the loop: **the sink is what declares its persistable fields, and that
declaration is what makes controls editable (§1).** "Who owns persistence" and "what may I
edit" are the same answer read from two ends.

🔴 **Cost accepted, and it is a standing obligation on 0006:** two sinks exist from day one
and both must be exercised, or the seam decays into "`IPendingChangeStore`, plus an interface
nobody else implements". A single-implementation abstraction is not a proven seam.

### 8. What this ticket did NOT do

No production code. No new public API entries — the surface remains 230
`PublicAPI.Unshipped.txt` entries as 0004 left it. No test run was required or performed;
nothing was built. The contract above is the deliverable, and 0006 fans it in with 0003 and
0004 into the build-ready specification.

### 9. Consequences for 0006

- **The public surface will grow by an editing capability type, a change-proposal type, and a
  sink interface.** 0006 must decide whether they ship in `Twig.Domain` alongside the
  document or in a separate assembly — the same question already open for `FallbackFormLayout`
  and `WorkItemTypeAppearance`, and now with more weight behind splitting, since a read-only
  consumer's IntelliSense would otherwise carry an editing vocabulary it will never call.
- **§3's advisory-not-authoritative caveat is a documentation obligation**, not just a design
  note. A host that mistakes the offered transition list for a guarantee ships a bug report.
- **§7's two-sink obligation is an acceptance criterion**, not advice.
- **§6's conflict report shape is the largest unspecified surface remaining.** It carries
  remote values, so it is a document-shaped payload, and 0006 should say whether it reuses
  `WorkItemDetailDocument` or a narrower carrier.
