# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the
codebase.

**Layout: single-context.** One `CONTEXT.md` at the repo root, ADRs under `docs/adr/`. There is
no `CONTEXT-MAP.md` and this repo is not a monorepo.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root — the domain glossary. It is authoritative for **names**.
- **`docs/adr/`** — read ADRs that touch the area you're about to work in. This directory does
  not exist yet; it will be created lazily when a decision is actually recorded.

If any of these files don't exist, **proceed silently**. Don't flag their absence; don't suggest
creating them upfront. The `domain-modeling` skill creates them lazily when terms or decisions
actually get resolved.

## Decisions also live in `wayfinder/`

🔴 This repo is unusual: `docs/adr/` is **not** the only decision surface, and today it is not
even the main one. Architectural and domain rulings are recorded as **wayfinder tickets**:

- `wayfinder/map.md` + `wayfinder/tickets/NNNN-*.md` — the architecture map. Destination:
  *decisions*, not shipped work.
- `wayfinder-1.0/map.md` + `wayfinder-1.0/tickets/NNNN-*.md` — destination: 1.0 shipped.
  Consumes the first map's *Decisions so far* as settled input.
- `docs/specs/` — specifications.

Before proposing a structural change, check the relevant map's **Decisions so far** and its
**Not yet specified** section. A ruling there has the same force as an ADR, and several carry
measured evidence that a short ADR could not hold.

Treat a contradiction with a wayfinder ruling exactly as you would an ADR conflict — see
"Flag ADR conflicts" below.

## Use the glossary's vocabulary

When your output names a domain concept (in a work item title, a refactor proposal, a
hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the
glossary explicitly avoids.

`CONTEXT.md` states its own naming rules, and they bind:

1. **A concept has one name.** If two names exist for one thing, one is wrong — fix the code.
2. **The code is the tiebreaker.** If a doc and a type name disagree, the type name wins and
   the doc is rot. `docs/` has known drift.
3. **Don't invent a name to avoid a rename.** New synonyms are how `Workspace` got three
   meanings.

Its §8 *Names the code does NOT use* is a live trap list — `Note`, `PendingChange`, `Seed` as a
type, and bare `Workspace` are all wrong. Read it before naming anything.

Two vocabulary facts that catch people out:

- **`Workspace` is being retired**, not disambiguated. The replacements are **Connection**
  (one `{org}/{project}` ADO endpoint) and **Bench** (a named, durable, switchable saved
  backlog). Always qualify `Workspace` until it is gone. `Sprig` is **reserved** for a future
  planning-over-seeds mode — do not spend it on a synonym.
- **A seed is not a type.** It is `WorkItem.IsSeed` plus a negative id.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing
language the project doesn't use (reconsider) or there's a real gap (note it for
`domain-modeling`). `CONTEXT.md` says it directly: when a concept needs a new name, add it
there first.

## Architecture vocabulary is separate

`CONTEXT.md` §10 draws the line deliberately: that file names the **domain**, and the
`codebase-design` skill names the **structure** (module, interface, depth, seam, adapter,
leverage, locality). Use the deep-module vocabulary for structural discussion rather than
"component / service / API / boundary". Do not add structural terms to `CONTEXT.md`.

## Flag ADR conflicts

If your output contradicts an existing ADR **or a wayfinder ruling**, surface it explicitly
rather than silently overriding:

> _Contradicts wayfinder 0001 (`Workspace` retired in favour of Connection + Bench), but worth
> reopening because…_

Cite the ticket or ADR by number. Both maps' Notes sections say the same thing in their own
words: a ticket arguing about a cost should **cite** the ticket that measured it rather than
assert.
