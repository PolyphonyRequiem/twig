---
id: 1004
title: Read the ADO work item form layout, and export it to disk
type: execute
status: open
blocked_by: []
tracked_in: [242, 247, 253]
---

## Why

[What is the 1.0 TUI?](1003-what-is-the-1-0-tui.md) banked a scope call: the TUI's editor
is **driven by the server's work item form layout**, in 1.0. ADO exposes the form's tabs
(pages), boxes (groups), and ordered fields (controls) per work item type, under the
process the project uses.

Two consequences, and only one of them is throwaway:

1. **Fetching and parsing the layout is production code.** If the editor is server-driven,
   twig reads layouts at runtime. That half ships regardless.
2. **Writing it to disk is the thin part** — but it is what unblocks design work. The
   owner's real layout lives behind a work data boundary and can only be pulled from a
   sandbox. An export command lets him run it there, review exactly what leaves, and hand
   over a structural file with no work item content in it.

So this is not scaffolding for a mockup. It is the first slice of the editor, with a
small command on top.

## Scope

- A layout capability: fetch the form layout for a given work item type on the current
  Connection, and parse it into a domain shape (pages → groups → controls, ordered, with
  labels, field reference names, and visibility).
- A command that writes that to a file.
- Both experiences, per the map's standing rule — the export is inherently script-shaped,
  so do not let it become interactive-only or rich-output-only.

## Open questions for whoever runs it

- **Do stock (system) processes return a layout, or only inherited/customized ones?**
  Reported to work for inherited processes; unverified for out-of-the-box ones. If stock
  processes refuse, that is a real constraint on the server-driven editor decision and must
  come back to 1003, not be worked around quietly.
- Whether the export writes one work item type or all of them. One is enough for the
  design work that motivated it.
- Control kinds that have no terminal equivalent (rich text, links grids, attachments,
  history) are **not** this ticket's problem — but the parse must preserve enough to know
  what kind each control is, or the renderer cannot decide later.

## Not in scope

- Rendering. That is a separate ticket, and its input is this ticket's output.
- Any work item values. This reads structure only.

---

## Ruling — does `twig process layout` survive its overlap with `twig process description`? (AB#242)

**Status: MEASURED. The decision itself is Daniel's and is recorded below once made.**

`docs/specs/process-description.spec.md` (branch `docs/process-descriptor-map`) carried exactly
one open question, deferred to the build by Daniel on the grounds that the overlap should be
**observed rather than predicted**. It is now observable. Everything in this section is from real
runs against the live `Niflheim` process (14 types, org `PolyphonyRequiem`, project `Twig`), not
from reading the code.

### How it was measured

```bash
twig process description --out desc.json -o json          # whole process, 14 types
twig process layout <display-name> --out lay.json -o json # once per type, 14 times
```

Both outputs were then compared structurally: every layout control was reduced to its
`(page, section, group, controlId)` tuple in both documents and the sets and sequences compared.

🔴 **The whole measurement was re-run from scratch after this branch was rebased onto `9bbd5bdd`**
(PR #377, the Spectre.Console 0.54.0 → 0.57.2 bump, which touches `RendererFactory` — shared by
both verbs' output path). Every structural and code figure below reproduced exactly; only the
timings moved, and they were re-taken with a larger sample. See §4.

### 1. The data overlap is TOTAL, and it is a strict superset

| | `process layout` (14 runs) | `process description` (1 run) |
|---|---|---|
| Field controls emitted | **117** | **117** |
| Same control set, per type | — | **identical, 11/11 servable types** |
| Same control ORDER, per type | — | **identical, 11/11** |
| Page ids identical | — | **11/11** |
| Group ids identical | — | **11/11** |
| System controls emitted | **0** | **99** (9 per type) |

🔴 **The description emits every control the layout command emits, in the same order, and 99 rows
it does not.** There is no control, page or group the layout command reports that the description
omits. The overlap is not partial.

Per-row attributes, comparing the two documents' own key sets:

| Row | Attributes in `layout` | Extra attributes in `description` |
|---|---|---|
| page | 6 | `inherited`, `order` |
| group | 5 | `inherited`, `order`, `section` |
| control | 7 | `inherited`, `order`, `section` |

The layout command carries **no attribute the description lacks**. `section` survives in the
layout command only as a nesting level rather than a named value; the description carries it as
an explicit key on each row.

### 2. Where the two genuinely differ — three real differences, not one

**a. Type addressing is inconsistent between the two verbs.** `process layout` takes a **display
name** (`Task`); `process description` takes a **reference name** (`Niflheim.Task`).
`twig process description Task` fails with *"Work item type 'Task' does not exist in this
process"*. This is a live inconsistency in the same command family and is worth fixing whichever
way #242 goes.

**b. Locked system types.** Three of the 14 (`TestCase`, `TestPlan`, `TestSuite`) are locked and
the layout route answers **400 VS403115**, not 404.

- `process layout "Test Case"` → **exit 1**, raw server error, no output.
- `process description` → those types appear with `unfetched: formLayout` and the document still
  carries the other 11. **The description degrades; the layout command fails.**

**c. Shape and audience.** `layout` emits a nested tree plus a readable indented human rendering
of the form (~22 lines for Task). The description emits **flat** rows carrying their full path,
and its human rendering is the deliberately-abridged one-line-per-type summary — it prints **no
layout detail at all**. Reading one type's form in a terminal is served by `layout` today and by
nothing else.

### 3. Code overlap is SMALLER than the data overlap suggests

Non-comment, non-blank lines:

| Component | Lines | Shared? |
|---|---:|---|
| Wire DTO `AdoFormLayoutResponse.cs` | 73 | ✅ **shared by both paths** |
| Route + pinned api-version `AdoApiVersions.ProcessLayout = "7.1"` | 1 | ✅ **shared constant, two call sites** |
| `FormLayout.cs` (layout's value object) | 31 | layout only |
| `ProcessDescriptionLayout.cs` (description's value object) | 33 | description only |
| Fetch + map, `AdoIterationService` | 122 | layout only |
| Fetch + map, `AdoProcessDescriptionSource` | 70 | description only |
| Render, `ProcessLayoutCommand.BuildLayoutTree` | 82 | layout only |
| Render, layout block in `ProcessDescriptionDocument` | 76 | description only |
| `ProcessLayoutCommand` shell (validation, `--out`, errors) | 55 | layout only |

**Shared: 74 lines (the DTO and the route constant). Duplicated-in-spirit: ~190 lines of
fetch/map and ~158 lines of render.** So the duplication is real but it is **parallel
implementation over a shared wire contract**, not copy-paste — and `ProcessDescriptionLayout`'s
own remarks already record why the split exists: `FormLayout` does **not carry the server's
`order` key**, and the description cannot be byte-stable without it. Adding `order` to
`FormLayout` would change a shipped **public** record's constructor.

🔴 **`FormLayout` is not the layout command's private type.** It has **15** referencing files, and
three of them are the TUI (`DetailDocumentSource`, `WorkItemFormView`, `Program`) plus
`WorkItemDetailProjector` and `FallbackFormLayout`. Deleting the layout *command* frees the
command shell and its renderer — **~137 lines** — and nothing else. The fetch path is production
code for the 1.0 server-driven editor and ships regardless; this ticket says so at the top.

### 4. Cost of the overlap, measured

Eight runs of each, on the rebased head (`main` @ `9bbd5bdd`, after the Spectre.Console 0.57.2
bump touched the shared renderer factory):

| Invocation | min / median / max | Bytes |
|---|---|---|
| `process layout Task` | 1.22 / **1.31** / 1.40 s | 8,360 |
| `process description Niflheim.Task` | 1.65 / **1.71** / 1.89 s | 50,133 |
| `process description` (whole, 14 types) | 2.75 / **2.84** / 2.90 s | 508,793 |

🔴 **Eight samples rather than three, deliberately.** A first pass took three each and produced a
0.4 s gap that a second pass did not reproduce; run-to-run spread on three samples is wide enough
to swamp the difference being claimed. On eight, the one-type gap is **~0.40 s** and stable.

Reading one type's form via the description costs **~0.4 s more and 6× the bytes**, and the human
rendering of it carries **no layout detail whatsoever**.

Test surface: `ProcessLayoutCommandTests` (345 lines) + `ProcessLayoutSampleExportTests`
(209 lines) = **554 lines** attributable to the command.

### The two shapes

**Shape A — `layout` survives as its own command, and the inconsistencies are fixed.**
Keep both verbs. Treat the ~137 duplicated command-and-render lines as the accepted cost the
separate-verb ruling already priced in, and spend a small follow-up on the three measured
differences: accept a reference name as well as a display name, and stop failing hard on locked
types (report them the way the description does).

- *For:* the layout command is the **only** surface that renders a readable form to a terminal —
  the description's human rendering is abridged by binding ruling and shows none of it, and
  Decision 10 explicitly **forbids** per-part selection that would let the description serve
  "just the layout". It is 6× cheaper for the one-type case, it is the input the 1.0 editor work
  was built around, and ~~it is `internal` so nothing public is frozen by keeping it~~.

  > **Correction (AB#253):** the struck clause was false when written — `FormLayout` had been
  > `public` since AB#155 (2026-08-09). Struck entirely rather than rewritten, because it was
  > never load-bearing: Shape A won on the readable-rendering and cost arguments above, and
  > the ranking does not change without it.
- *Against:* two renderers over one wire payload stay in the tree, and can drift.

**Shape B — `layout` becomes a view onto the description.**
Delete the command's own fetch/render path and have `process layout <type>` render the layout rows
out of the assembled description document.

- *For:* one fetch path, one ordering authority, ~137 lines and one renderer gone; `order` and
  `inherited` arrive at the layout surface for free.
- *Against:* it makes the cheap one-type read pay the description's assembly cost; the
  description's layout rows are **flat and path-prefixed**, so the readable indented rendering has
  to be rebuilt from them anyway (the ~82 lines come back in a different file); and it couples a
  1.0-editor-adjacent command to a `0.1` document whose own spec says the layout shape is still
  under design. It also brushes against Decision 10 — a layout-only view *is* per-part selection,
  even if it is a separate command rather than a switch.

### Recommendation

🔴 **Shape A, ranked first, and not narrowly.** The overlap that was feared is a *data* overlap and
it is total; the overlap that actually costs anything is ~137 lines of command-and-render code
over a **shared** DTO and route. Against that, `layout` is the only surface that renders a form a
person can read, and the ruling that made the description's human rendering abridged is the same
ruling that stops the description ever replacing it. Shape B pays real coupling for a saving that
mostly reappears elsewhere.

The honest tidy-up is not a merge — it is the **three measured differences** in §2, which are
worth their own ticket regardless of which shape is chosen.

---

## 🔴 RULED — Shape A. `twig process layout` survives as its own command.

**Ruled by Daniel, 2026-08-12, on the measurement above.** The open question in
`docs/specs/process-description.spec.md` is now CLOSED and must not be reopened in review.

**The decision:** both verbs stay. The overlap is accepted, exactly as the separate-verb ruling
priced it — and the measurement shows the cost is smaller than the overlap's appearance
suggested, because what the two verbs share is the wire contract rather than the implementation.

**What follows from it:**

- Nothing is deleted and nothing is merged. `ProcessLayoutCommand`, `FormLayout`, and the
  layout fetch path all stay where they are.
- ~~`FormLayout` stays `internal`, per Implementation Decision 9.~~ **Superseded — see the
  AB#253 ruling at the foot of this file. `FormLayout` is `public`, and correctly so.** The
  substance of this bullet survives: keeping the layout command does not freeze the type.
  What was wrong was the visibility it asserted, and the drift diagnosis beneath it.

  > 🔴 **This line records real DRIFT, discovered while building AB#247 — not a mistake in the
  > ruling.** Implementation Decision 9 does say it, naming the type explicitly:
  > *"`ProcessRule` (with its condition and action types) and `FormLayout` stay `internal`.
  > Neither goes through the public-API/SemVer mechanism now"*
  > (`docs/specs/process-description.spec.md`, branch `docs/process-descriptor-map`), and it
  > ranks the two — *"If only one is promoted later, promote the rule type first"* — which only
  > parses if `FormLayout` is in its scope.
  >
  > **The code disagrees.** `FormLayout` is `public sealed record`
  > (`src/Twig.Domain/ValueObjects/FormLayout.cs`) and its whole surface is declared in
  > `src/Twig.Domain/PublicAPI.Unshipped.txt` — it goes through exactly the mechanism Decision 9
  > says it does not. Which is correct is **not AB#247's call to make**: it is a live conflict
  > between a decision and the tree. **Tracked as AB#253.**
  >
  > **Tracked as AB#253 — now RULED. See the ruling at the foot of this file: it was not
  > drift, and the code was right.**
  >
  > What AB#247 did about it: **nothing to the type's visibility.** `SystemControls` was added as
  > a **non-breaking init-only member** with a `PublicAPI.Unshipped.txt` entry rather than as a
  > positional parameter — the shape that is correct under either resolution. It does add two
  > declared entries to the public surface, but it makes **no shipped SemVer promise** (nothing
  > moves to `PublicAPI.Shipped.txt`), breaks no existing construction site, and does not block a
  > later demotion to `internal`. Nothing in the AB#242 ruling above depends on the answer; that
  > ruling is that the command survives, and the type's visibility does not bear on it.
- The three measured inconsistencies in §2 are scheduled as **AB#247** — display-name vs
  reference-name addressing, the hard failure on locked types, and the layout command's missing
  system controls. They are defects in the layout command in their own right, not overlap
  tidy-up, which is why they survive this ruling rather than being closed by it.

**What this ruling does NOT license.** It is not a statement that duplication here is free. If
the two renderers drift — if a future change lands in the description's layout rows and not the
layout command's, or the reverse — that drift is the cost this ruling accepted, and the answer is
to fix the drift, not to reopen the merge question. The measurement is preserved above so a later
reader can re-run it rather than re-argue it.

---

## 🔴 RULED — `twig process layout` resolves against the PROCESS's type roster. (AB#247)

**Ruled by Daniel, 2026-08-12, on the measurement below.** This ruling was NOT anticipated by
#242. It came out of building AB#247's first item, and it makes that item a bigger change than
"accept both name spellings".

### What the measurement found

§2a above recorded the two verbs as disagreeing about *how a type is spelled* — display name
versus reference name. That was **incomplete**. They disagree about *which roster a type comes
from*, and the two rosters give the same type **different reference names**:

| | route | types | `Task` resolves to |
|---|---|---|---|
| `process layout` | `{project}/_apis/wit/workitemtypes` | **20** | `Microsoft.VSTS.WorkItemTypes.Task` |
| `process description` | `_apis/work/processes/{id}/workItemTypes` | **14** | `Niflheim.Task` |

Three types collide — `Task`, `Issue`, `Epic` — the three this process **inherits and
re-parents**. For those, `twig process layout Task` was fetching and reporting the **stock parent
type's** form, labelled with the parent's identity.

**It was harmless in content, and that is what made it dangerous.** All three collision pairs were
diffed control-by-control against the live org and matched exactly: same field-control sets in the
same order (Task 10 controls, Issue 13, Epic 11 — identical parent-vs-child in each case), same
page ids, and the same 9 system controls. The layouts agree because nothing has customized the
child forms yet. **The first person to edit one gets the stock form served silently, with no
marker.**

This is Implementation Decision 11's trap one layer down — *"the project named Twig does not run
on the process named Twig"* — and it is the same reason the description resolves by process
reference name.

### The decision

`process layout` resolves against the **process roster**, and accepts **either** the display name
or the process reference name. Reference name is matched first, as the stable identity; display
name second, as the convenience.

**What follows from it:**

- `twig process layout Task` now reports `Niflheim.Task`, not
  `Microsoft.VSTS.WorkItemTypes.Task`. This is a **behaviour change on a shipped command**, and
  it is the point of the fix rather than a side effect.
- 🔴 **The stock parent form is no longer reachable from this verb at all** — not even by naming
  it in full. Resolution matches rows of the process roster only, and
  `Microsoft.VSTS.WorkItemTypes.Task` is not a row in it; the parent is named only by the
  child row's `inherits` field, which resolution does not consult. Verified live:
  `twig process layout Microsoft.VSTS.WorkItemTypes.Task` reports no layout available.
  **This is a real loss, accepted deliberately** — the verb describes the process's form, and
  the parent's form is not it. If reading a parent form is ever wanted, following `inherits` is
  a separate change and a separate decision.
- Six project-only types also leave the layout command's reach: `Shared Steps`,
  `Shared Parameter`, and the code-review and feedback request/response pairs. They are not in
  the process's roster, so the process layout route does not serve them.
- The two verbs now agree on **what a type is**, not merely on what you may type.

### The ambiguity question, measured rather than assumed

AB#247's brief flagged that accepting both name forms needs a rule for the ambiguous case, and
reserved that rule for Daniel. Measured against the live org, **the ambiguity does not currently
exist**:

- No display name is also any type's reference name (0 of 20 project + 14 process rows).
- No display name is duplicated within either roster.

So no tie-break rule was invented. The reference-name-first ordering is a **defined answer for a
case that cannot presently arise**, not a ruling on it — if a real collision ever appears, the
rule for it is still Daniel's to make.


---

## 🔴 RULED — `FormLayout` is PUBLIC. Implementation Decision 9 is narrowed to the rule types. (AB#253)

**Ruled by Daniel, 2026-08-12.** AB#253 was filed as *"resolve the drift"* between Decision 9
and the tree. **The premise was wrong: there is no drift.** There are two closed rulings that
disagree, and the later one wins.

### What the card assumed, and why it did not survive contact

The card — and the note above it — read the asymmetry as evidence of an accident: Decision 9
named two types, `ProcessRule` obeyed, `FormLayout` did not, so `FormLayout` slipped. That
reading is the natural one and it is **falsifiable**, so it was tested rather than argued:

```bash
git log --oneline -S "public sealed record FormLayout" -- src/Twig.Domain/ValueObjects/FormLayout.cs
# 25d9f59d feat(projection): prove the detail document in a real external host AB#155
```

**Exactly one commit** — under the pathspec shown; unscoped, this branch's own AB#253 commit
also matches. And it is not an accident. It is
`wayfinder-detail-projection/tickets/0003-real-external-host-probe.md` (`status: closed`),
whose own "What shipped" table names the change in its own words:

> `src/Twig.Domain/ValueObjects/FormLayout.cs` — `internal` → `public` on all five layout
> records. **Shape unchanged** — accessibility only, exactly as ticket 0001 scoped it.

Ticket 0001 (`status: closed`) recorded the five records as `internal` at the time and scoped
the promotion deliberately. So the promotion was **decided, reviewed, scoped, and shipped**.
Decision 9 was not violated by carelessness; it was **overtaken by a later decision made when
a real external consumer existed that had not existed when Decision 9 was written**.

### Why demotion was not merely undesirable but impossible as scoped

The card scoped the work as five records plus 105 manifest lines. Attempted, it does not
compile — three errors, all **inside `Twig.Domain`**, before the sample host is even reached:

```
FallbackFormLayout.cs(77,30):      error CS0050  return type 'FormLayout' is less accessible than 'FallbackFormLayout.For'
FallbackFormLayout.cs(103,24):     error CS0051  parameter type 'FormLayout' ... 'FallbackFormLayout.IsFallback'
WorkItemDetailProjector.cs(44,42): error CS0051  parameter type 'FormLayout' ... 'WorkItemDetailProjector.Project'
```

`WorkItemDetailProjector.Project` exposes a `FormLayout` **in its public signature** and is the
entire public
projection entry point — the one `samples/Twig.DetailHost/Program.cs` calls as an external
consumer. Demoting the layout records therefore forces demoting `WorkItemDetailProjector`,
`FallbackFormLayout`, and transitively the whole `WorkItemDetailDocument` family, which
deletes the boundary AB#155 shipped and removes the reason the sample project exists.

🔴 **That is a boundary reversal, not a visibility fix**, and the card did not price it.

### The decision

**`FormLayout`, `LayoutPage`, `LayoutSection`, `LayoutGroup`, `LayoutControl` stay `public`.**
No code changes. Implementation Decision 9's visibility clause is **narrowed to `ProcessRule`
and its condition and action types**, which remain `internal` and are unaffected by this
ruling.

Decision 9's underlying argument is not repudiated — a public type does assert a stability the
`0.1` document warns about. It is **outranked**: a proven external consumer is a stronger
claim on the boundary than a document version number, and the consumer did not exist when the
argument was made.

### Correction to Decision 9's ranking, for whoever reads it next

Decision 9 said: *"If only one is promoted later, promote the rule type first."* **Reality went
the other way round.** The layout type was promoted and the rule type was not, because the
promotion was driven by a consumer that materialized rather than by the ranking. The ranking
is therefore **stale as a prediction**. It is not wrong as a statement about design settledness
— the rule type remains the simpler mirror of the wire payload — but nobody should read it as
describing what happened.

### `ProcessRule`'s visibility, measured (the card's third acceptance criterion)

Measured directly on this branch rather than taken on report:

| Type | Declaration | `PublicAPI.Unshipped.txt` entries |
|---|---|---|
| `ProcessRule` | `internal sealed record` (`ProcessRule.cs:42`) | **0** |
| `RuleCondition` | `internal sealed record` (`ProcessRule.cs:71`) | **0** |
| `RuleAction` | `internal sealed record` (`ProcessRule.cs:73`) | **0** |
| `RuleCustomizationKind` | `internal enum` (`RuleCustomization.cs:93`) | **0** |

**Consistent with Decision 9 as narrowed. No action needed.** Worth recording for a future
promoter: publicising `ProcessRule` alone does **not** compile — its constructor exposes
`RuleCondition`, `RuleAction` and `RuleCustomization`, so the whole family moves together or
not at all (six `CS0051`s, measured).

### The enforcement point, and what it does NOT claim

`tests/Twig.Domain.Tests/Architecture/PublicProjectionBoundaryTests.cs`.

🔴 **Stated precisely, because overstating a guard is how it earns undeserved trust.** A
demotion was already caught twice before this file existed, and both are earlier and louder:

| Reversal | Caught by | Measured |
|---|---|---|
| **Partial** — the five layout records alone | the **compiler** | 3 × `CS0050`/`CS0051` in Twig.Domain; the assembly never builds, so no test runs |
| **Completed** — projection contract taken down too, so it compiles again | **`PublicApiAnalyzers`** (`RS0017`, `TreatWarningsAsErrors`) | **924** errors, one per manifest entry that would no longer be public |

So the visibility assertions are a **named failure in front of a cryptic one**, not the only
line of defence. What genuinely earns its place, and what no analyzer duplicates, is
`TheProjectionEntryPoint_ExposesFormLayoutInItsPublicSignature`: nothing else notices if
`Project` stops taking a `FormLayout`, at which point the records would be public for no
surviving reason and every other assertion would keep passing while the rationale expired.

**One real hole was found in review and closed.** The first version pinned only the five
layout records. Reflection reports a `public static` method on an `internal` type as public,
so demoting `WorkItemDetailProjector` itself would have kept the whole file green while
deleting the boundary it exists to protect. The projection types are now pinned too —
verified by mutation: demoting the projector fails `TheProjectionBoundaryIsPublic` **by
name**, where the first version passed.

Guards verified by mutation rather than assumed:

- Promote the rule family → the five `RuleTypesStayInternalPerDecision9` cases fail **by
  name**; `RS0016` fires independently on every member.
- Demote `WorkItemDetailProjector` → `TheProjectionBoundaryIsPublic` fails **by name**.
- Add a sixth public `Layout*` record → `TheLayoutSurfaceHasNotGrownUnnoticed` fails and
  **names the intruder**, so the inventory cannot silently narrow.
- Demote `FormLayout` → the three `CS0050`/`CS0051` errors above (compiler, not this file).

`IsVisible` is used rather than `IsPublic` throughout: `IsPublic` reports `false` for a
**public nested** type, which is externally reachable — so a future refactor that nested one
of these would break the positive arm spuriously and, worse, stop the negative arm firing at
all.

### 🔴 Found while verifying this card: the sample host's self-check never runs in CI

Ticket 0003 built `samples/Twig.DetailHost` with a `CheckAcceptanceFloor` that returns exit 1
on any miss, precisely so *"the sample cannot decay into a demo that prints something
pleasant"*. That guarantee is **not currently enforced anywhere**:

```bash
grep -c "DetailHost\|PROBE\|samples" tools/run-tests.sh .github/workflows/ci.yml
# tools/run-tests.sh:0
# .github/workflows/ci.yml:0
```

The project is in `Twig.slnx`, so CI **compiles** it — which is what catches a visibility
regression, and is the property this ruling actually leans on. But nothing ever **runs** it,
so the acceptance floor it carries is dead weight: the fixture could stop exercising all three
field states tomorrow and no check would notice.

**Pre-existing, not introduced by AB#253**, and deliberately not fixed here — this card had no
business widening into CI configuration. Recorded so the claim above is not read as stronger
than it is: *the boundary compiles in CI; it was run by hand once, during this card*
(`PROBE OK`, exit 0, under `DOTNET_ROLL_FORWARD=Major` because the sample targets `net10.0`
GA and no GA runtime is installed on this box). **Worth its own work item** to add a
`dotnet run --project samples/Twig.DetailHost` step.
