# Bench / Workspace unification — wayfinder map

## Destination

One vocabulary and one store for "what work am I looking at". A person can say what they are
working on, return to it, and switch between arrangements — without needing to know that `bench`
and `workspace` were ever different words, or that pins were once a file.

Reached when: there is exactly one command family for choosing what you see, exactly one durable
home for it, and no command that appears to work while doing nothing.

## Notes

- Domain vocabulary: `CONTEXT.md` §4 is authoritative. **Connection** = one `{org}/{project}` ADO
  endpoint. **Bench** = a named, durable, saved backlog holding **selectors** (rules, never
  results). `Workspace` is being **retired**, not disambiguated.
- The Bench arc shipped as ADO #144–#151, all `Done`, specified in `docs/specs/bench.spec.md`
  (wayfinder ticket 1007). **That spec wins over 1007 where they disagree.**
- This map is about **finishing and simplifying** what those tickets started. It is not a
  re-litigation of what a Bench is — that is settled.
- This is a PLANNING map. Produce decisions, not deliverables. One ticket per session.

## Where the seam actually is — measured, not assumed

Verified against `main` @ `748b3634` by reading the wiring, not the ticket titles:

| Surface | Backed by | Notes |
| --- | --- | --- |
| `bench create/list/switch/delete` | `SqliteBenchRepository` (durable) | complete |
| `twig workspace` (the view) | projection of the current Bench | `WorkspaceCommand.cs:315` says so |
| `workspace track/track-tree/untrack` | **BOTH** `FileTrackingRepository` and `PinWorkflow` | see below |
| `workspace area/sprint` | Connection config | not bench membership |
| `workspace exclude/exclusions` | file — and **inert** | nothing subtracts exclusions |

🔴 **`TrackingCommand` takes both `ITrackingService` and `PinWorkflow` and writes to both.**
That is not a defect — ticket #145 specified exactly this coexistence: *"pins in the file and
selectors on the Bench coexist; the file remains the live source for the existing commands."*
The migration (#146) then carried existing pins onto the default Bench.

**So the double-write is a deliberate transitional state that nobody has yet closed out.** Two
stores hold the same truth, and the file is still authoritative. That is the classic shape behind
this repo's recurring bug: a mirror with no compiler forcing the copies to agree (see AGENTS.md
on `WorkspaceContextFactory`, and 0007).

⚠️ **A caution learned while writing this map.** An earlier reading of this same code concluded
"pins never moved to Bench" from a grep that missed the second constructor parameter, and a
reading before that concluded "pins fully moved" from the ticket *title*. Both were wrong in
opposite directions. **Read the constructor and the registration, not the card and not one
grep.**

## Not yet specified

Candidate tickets. Each needs sharpening before it is worked.

### A. Does `workspace` survive as a word at all?

The view verb is the weakest case for keeping it: `twig workspace` renders the current Bench, so
the noun is already stale. But `area` and `sprint` are **Connection config**, not bench
membership — they may belong under neither `workspace` nor `bench`. Decide the whole family at
once rather than renaming the easy half.

### B. Close the pin double-write

Pins are written to two stores with the file authoritative. Deciding this means deciding which
store wins, whether the file is deleted or retained for exclusions, and what happens to a person
mid-transition. **A silent break is not available here** — #146's own brief says pins fail
silently and are discovered weeks later.

### C. What to do about `exclude`

`workspace exclude` accepts input, persists it, and **changes nothing** — nothing subtracts
excluded items from the view. Three options, and they are genuinely different: delete the
command, implement subtraction, or leave it and document the inertness. Ticket 1007 warns that
implementing it inside the Bench would be *"specifying a behaviour for the first time wearing
the costume of a data move."*

🔴 A command that exits 0 having done nothing is this repo's named defect class. Whatever is
decided, the current state cannot stay undocumented.

### D. Is a subtree pin live or snapshotted?

`CONTEXT.md` describes a subtree pin as matching an item and its descendants "as they are now",
while #145's acceptance criteria demand that *"a subtree selector matches a child created AFTER
the selector was added"*. These readings may or may not conflict. **Verify empirically in the
sandbox before writing either into a skill.**

### E. What the CLI should look like afterwards

Only answerable once A–D are decided. The consumer of this answer is a `twig-benches` skill,
which is **on hold** until then — writing a skill over an unfinished seam teaches the seam.

## Decisions so far

<!-- one line per closed ticket -->

## Out of scope

- Redefining what a Bench is. Settled by 0022 and `docs/specs/bench.spec.md`.
- Context (the disposable place to stand). Its own concern, its own schedule. #151 says
  explicitly: do not fix the shared active-item slot in passing.
- Reconciliation and sync boundaries. A Bench is never a sync unit (0004 §2).
