---
id: 0004
title: Volume, and whether a human ever reads this
type: grilling
status: open
blocked_by: [0002]
claimed_by:
---

> 🔴 **SUPERSEDED — tracked on the board as [#222](https://dev.azure.com/PolyphonyRequiem/Twig/_workitems/edit/222).**
> Do not edit or re-sync this file. Kept for git history only.

## Question

Does the descriptor have a human rendering, and what happens to the all-types case?

The reporter's full REST descriptor was **1.09 MB across 15 types**. This workspace's own
process has 17 types and 85 project-wide fields. `twig process` with no type argument lists all
types today, which raises an immediate question this map must answer rather than discover
during the build: does `--descriptor` with no type mean *every type*, and is that a single
multi-megabyte document?

Sub-questions:
- Is human output a summary, a refusal ("use `-o json`"), or the full thing?
- Does the all-types case exist at all, or is a descriptor always per-type?
- Is `--out <file>` the right escape, matching `twig process layout --out`? That command already
  writes the chosen format verbatim to a file and keeps stdout clean, which is a working
  precedent in the same command family.
- Does anything need caching, or is a live fetch acceptable at 0001's measured call count?

## Answer

<!-- empty until resolved -->
