---
id: 0004
title: Are Change, Validation and Documentation still the right Work-level types?
type: grilling
status: open
claimed_by:
blocked_by: [0002, 0003]
---

## Question

A prior session agreed **Change**, **Validation** and **Documentation** as Work-level types.
**None was ever created.** Are they still the right names and the right set, now that
`Request for Change` (0005) is on the table and the board/markdown mapping (0002) is being
settled?

## Why this exists

@session:twig/20260821_175214_910f0c agreed these three plus `Finding` (ticket 0011). The
backlog level renames from that session landed; **the type creates did not** — confirmed live
this session against the 16-type list. So this is not a fresh proposal; it is an agreement that
never executed, revisited with more context than it had.

**This ticket is what AB#644 is blocked on.** Its five-unit implementation handoff needs a
Work-level type meaning *"a unit of work that can be pull-requested into main"* — proposed as
`Change` — and the cards could not be created because the type does not exist. The handoff is in
`Custom.WayfinderAnswer` on work item 644 (20,308 chars; §5 holds the five units); map #621 is
`Doing` with all five design children Done. **Do not create those five units here** — this map
rules, the build that follows creates.

That blockage is a reason to answer this ticket *well*, not quickly. A type created by reflex to
unblock a handoff is how the four dormant Request/Response types got there.

## Blocked on 0002 and 0003 — and here is why, not merely that

- **0002** decides whether Work-level types are the board's whole job or whether some work also
  lives as markdown. If rulings and specs are markdown-authoritative, `Documentation` may be a
  type with nothing to hold.
- **0003** supplies the demand-side test — *which role is worse off if this type does not
  exist?* `Validation` in particular reads as a process artifact; if no role can say when they
  would open one, that is the answer.

## What a good answer settles

- For each of `Change`, `Validation`, `Documentation`: keep, rename, merge, or drop — with the
  role and the moment that opens one.
- 🔴 **Why each is not just a `Task` or a `Feature`.** The existing Work level already holds
  `Bug`, `Feature`, `Grilling`, `Prototype`, `Research`, `Spec`, `Wayfinder Task`. A new type
  must be distinguishable from all of them by something other than its title.
- The `Change` ↔ `Feature` ↔ `Task` boundary specifically. "Pull-requestable unit" is a *size and
  delivery* claim, not a *kind* claim, and ADO backlog level governs display, not link legality
  (see the closed research). Is `Change` a type, or is it a `Task` with a convention?
- Whether the set is complete or whether 0003's evidence adds one.
- Which fields and gates each carries — **defer the ruling to 0007**, but state the requirement
  so 0007 has a customer.

## Do not

- Do not create the types. Do not PATCH the process. Out of scope for this map.
- Do not create AB#644's five units, however tempting once `Change` is ruled in.
- Do not accept "a prior session agreed it" as the argument. That session's agreement is input,
  not authority — and its failure to execute is itself evidence worth weighing.
