---
id: 0001
title: Is the destination the generic layer, the Hyperbright instance, or both in order?
type: grilling
status: closed
claimed_by: hermes/twig-process-types-02
blocked_by: []
---

## Question

When this map is done, what has been settled — twig's **generic** vocabulary for expressing any
customer's ADO process, **Hyperbright's** concrete type set, or both with the generic layer
first and our board as its first instance?

## Why this exists

The brief proposed a destination paraphrasing one sentence of Daniel's, then closed with a rule
that pulls against it:

> twig owns the generic systems for driving an ADO process; **the board's process is customer
> zero, not the product.** A design is right when it would still be right for a customer whose
> process we have never seen.

The proposed wording describes *our* type list. The closing rule describes a product. These
produce different tickets and a different sense of done:

- **Instance-only** ends at "Hyperbright has these N types with these fields and layouts". It
  is concrete, immediately unblocks AB#644, and risks shipping one team's taxonomy as though it
  were a design.
- **Generic-only** ends at "twig can express a process; here is the vocabulary". It honours the
  closing rule and leaves our board unsettled — which is the thing Daniel actually asked for.
- **Both, in order** settles the vocabulary and then instantiates it, treating our board as
  evidence the vocabulary works.

🔴 **The charting session took "both, in order" on its own judgement** because Daniel was not
available when asked, and marked the destination PROVISIONAL in the map. That is a placeholder,
not a ruling. **This ticket is where it becomes a ruling or gets overturned.** Resolve it first
— it rescopes every other ticket on the map.

## What a good answer settles

- The destination wording, replacing the provisional text in `map.md` verbatim.
- Whether a ruling that only makes sense for this team is a defect or an acceptable outcome.
- Whether "how each kind of team member uses twig" (ticket 0003) is generic-layer or
  instance-layer work — Daniel named it explicitly, so it must land somewhere.
- Whether the map's *Out of scope* boundary (no board mutation, no #615 build) survives the
  answer.

## Do not

- Do not resolve this by picking the answer that makes the other tickets easiest to write.
- Do not treat "both" as a free lunch: it is the widest scope, and if the map is to close in a
  reasonable number of sessions the answer must say what "the generic layer" stops at.

## Answer

Resolved with Daniel 2026-08-22. **The destination is the Hyperbright instance — one layer,
not two — and the generic rule is a GATE applied to every ruling rather than a layer built
before them.**

### The reasoning that overturned the provisional guess

The charting session read the brief's closing rule as a *deliverable* and made it layer 1. Read
as grammar it is not a deliverable:

> *"A design is right **when** it would still be right for a customer whose process we have
> never seen."*

*"A design is right when…"* is an **acceptance criterion applied to a ruling**. It is not an
instruction to produce a generic-vocabulary document. Reading a test as a layer is what made the
destination widest and least closable, because a second layer needs a stopping line that nobody
had drawn — the very gap this ticket's *Do not* section flagged.

**Measured evidence that the layer was never real.** Of the ten open tickets, *nine* are
instance questions in their own titles — 0002 (our board vs our markdown), 0003 (our team), 0004
(our Work-level types), 0005 (our Request for Change), 0006 (our ADRs), 0007 (our fields and
gates), 0008 (our form layouts), 0010 (our backlog levels), 0011 (our Finding type). **Zero
tickets were charted for layer 1.** The generic layer was promised in the destination and never
charted, so the map was already a single-layer map with an unfulfilled preamble. Option D
deletes a layer that no ticket used, which is why no ticket needed rescoping.

Four options were put; D was chosen:

| | Keeps | Deletes | Closes |
|---|---|---|---|
| **A** instance-only | 10 tickets | 0 | wave 5 — but the governing rule is honoured nowhere |
| **B** generic-only | ~2 | 7 tickets | unknown; our board stays unsettled, AB#644 blocked forever |
| **C** both, in order | 10 + N new | 0 | wave 5 + unknown — needs a boundary nobody has drawn |
| **D** instance + gate | **10, unchanged** | **0** | **wave 5** |

### The three sub-rulings

**1. Defect vs acceptable — the split is VOCABULARY vs MECHANISM.**

- **Acceptable** when only the chosen *value* is ours. "Our pull-requestable type is named
  `Change`" is our value; a customer choosing `Work Package` is served by the same mechanism.
  That is customer zero working as intended, not a defect.
- **Defect** when the *mechanism* is ours — twig could not express another customer's choice at
  all (e.g. twig hardcoding the name `Change`). The ruling names the missing generic mechanism.

**2. 🔴 The gate is a LEDGER, not a VETO. A defect verdict does not block the ruling.**

This is the load-bearing half. If a defect verdict stalled a ticket, the map would be blocked
behind ADO #615 — which is explicitly *Out of scope* — and could never close. So: the verdict is
recorded, the ruling stands either way, and the accumulated defect lines become #615's
requirements list. The map gets a spec for #615 for free without designing or building it.

**3. Ticket 0003 is EVIDENCE, not a gated ruling.**

A description of *our own team* is not the kind of claim "would this be right for a customer we
have never seen?" can sensibly judge — passing it through the gate would reject as
"only ours" the very thing that makes it useful. 0003 is recorded and closed normally; its
output is a **demand-side test** — *which role is worse off if this type does not exist?* —
applied by 0004, 0005 and 0011. This matches 0003's own instruction to *"feed the demand-side
evidence to 0004/0005/0011 and stop"*.

⚠️ **The accepted cost, recorded so it is not rediscovered as a surprise:** 0003 is now the one
part of the map carrying no generic pressure. If twig's *role model* should itself generalise
(roles as tags? area paths? a field?), 0003 would have to become a gated ruling. Daniel was
shown this counter-argument and accepted the trade.

### Consequences for the rest of the map

- **The `Out of scope` boundary survives unchanged**, with one clarification: the map now
  *collects* the defect lines into a requirements list for #615 rather than leaving them
  scattered per-ticket. Saying what #615 must express was already inside the line; the roll-up
  makes the accumulation explicit.
- **No ticket was rescoped.** All ten were written against "both, in order" but none *depends*
  on layer 1 existing — they are layer-2 questions already. The gate applies at resolution time
  and the Destination block states it, so editing ten ticket bodies to repeat it would be churn.
- **No new tickets, and no fog graduated.** The one fog patch this touches — *"what twig's
  generic policy engine (ADO #615) must express"* — is not sharpened by this ruling; it is now
  fed continuously by the gate, so it stays as fog with a note.
- **This ruling schedules no work**, so no `tracked_in` and no `tools/check-tracking.sh` run.

### Gate verdict on this ruling itself

**Acceptable.** The ruling decides *how this map judges its own rulings*; it hardcodes nothing
into twig and constrains no customer's process. The destination being Hyperbright's process is
the customer-zero premise stated, not a mechanism that excludes anyone.
