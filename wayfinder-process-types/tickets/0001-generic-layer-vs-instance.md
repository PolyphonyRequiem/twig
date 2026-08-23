---
id: 0001
title: Is the destination the generic layer, the Hyperbright instance, or both in order?
type: grilling
status: open
claimed_by:
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
