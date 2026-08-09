---
id: 0006
title: Synthesize the implementation handoff and acceptance gates
type: task
status: closed
claimed_by: build-handoff
blocked_by: [0003, 0004, 0005]
---

## Question

Reconcile the public boundary, document contract, external-host evidence, Twig TUI migration, and optional editing seam into one build-ready specification and sequenced implementation plan. Define package/API manifests, compatibility posture, fixture corpus, red-before-green tests, real consumer gate, migration slices, and explicit non-goals.

The final gate must trace the production chain `consumer → public projection → host-owned renderer` and reject a test that substitutes any link under review. Close the map only when no design decision remains for implementation.

## Answer

Pinned at commit `4969bf454f8d426da829a38a9674259aef9b3a7e` (`origin/main`, "docs(wayfinder):
settle the optional edit-capability contract AB#155"), working tree clean except this ticket
and the map. Every current-code claim below was read at that commit, and the starting state
was measured rather than assumed:

```
TWIG-VERDICT OVERALL: PASSED      Cli 3074 · Infrastructure 1416 · Mcp 1295 · Domain 1938
Twig.Tui.Tests                    Passed! - Failed: 0, Passed: 67          (not in run-tests.sh)
samples/Twig.DetailHost           exit 0, "PROBE OK"                       (needs DOTNET_ROLL_FORWARD=Major here)
```

**This ticket closes the map.** No design decision remains for implementation. What follows
is the specification a builder picks up.

---

### 1. The packaging call — ONE package. Do not split `Twig.Detail`.

**`PolyphonyRequiem.Twig.Domain` ships alone.** The editing capability type, the change
proposal type, the sink interface, the conflict report, `FallbackFormLayout` and the
projection all live in it.

0001 predicted the cost, 0003 measured it, 0004 and 0005 added weight to splitting. I read
all of it and the split still loses, for a reason that only becomes visible once you write
the second csproj:

🔴 **A `Twig.Detail` package would take a `ProjectReference` on `Twig.Domain` anyway.** The
projection is typed against `FormLayout`, `WorkItemSnapshot`, `FieldDefinition` and
`WorkItemTypeAppearance` — all Domain value objects, and all of them *inputs the consumer must
already hold to call `Project` at all*. So the consumer acquires both packages, restores both,
versions both, and sees the same ~30 interfaces in IntelliSense. **The split does not reduce
what a consumer acquires. It only changes which package name the five types sit under**, and
buys a version-skew pair and a second manifest for that.

The complaint 0003 recorded is real but is a **namespace** complaint, and the namespace is
already correct: the whole contract lives under `Twig.Domain.Projections.*`. A consumer types
`using Twig.Domain.Projections;` and gets the five types. The interfaces are one namespace
over, exactly where they were before this map started.

Conditions that would reopen this — stated concretely so a future reader does not reopen it
on taste:

- Domain acquires a runtime `PackageReference` (today: zero), making the leaf-node claim false.
- The projection stops depending on Domain value objects, so a split package could stand alone.
- A consumer reports the interface surface as an actual defect, not a predicted one. Three
  tickets predicted it; nobody has hit it.

### 2. The compatibility promise

**SemVer over `PublicAPI.Shipped.txt`, identical across both TFMs, starting at the first
external release.**

- The 230 `PublicAPI.Unshipped.txt` entries this map added are promoted to
  `PublicAPI.Shipped.txt` **in one commit, at release, and not before**. Until then the
  surface is explicitly unstable and the manifest says so by construction — that is what
  `Unshipped` means, and it is the last cheap window to change a name.
- After that promotion, removing or changing an entry is a **major**; adding one is a **minor**.
  The analyzer enforces this mechanically; no prose rule is required and none is written.
- 🔴 **The promise covers Domain's whole shipped manifest, not just the projection.** That is
  the honest price of §1's no-split call, and it is smaller than it sounds: those 1794 entries
  are already under the same analyzer discipline today, so the release changes who is watching,
  not what is tracked.
- **`net10.0` and `net11.0` carry the same surface.** The only TFM-conditional manifest is
  `PublicApi/net10.0/PublicAPI.Shipped.txt` for the `IUnion`/`UnionAttribute` polyfill
  (`CompilerPolyfill.cs`), which is a shim for a type the `net11.0` runtime ships itself. A
  consumer never writes against it. Any *other* TFM-conditional public entry is a defect —
  it would mean a consumer's code compiles on one target and not the other.

### 3. `FallbackFormLayout` stays public

0004 asked whether Twig's opinion about arrangement belongs on the consumer contract, noting
it is "closer in kind to `WorkItemTypeAppearance`", which 0002 §9 deliberately kept out.

**Keep it public.** The analogy is the thing to break, because it is comparing across two
different boundaries:

- 0002 §9 kept appearance out of **the document**. `FallbackFormLayout` is not in the document
  either — it is a separate static factory returning a `FormLayout`, i.e. one of `Project`'s
  *inputs*. Both decisions say the same thing: the document carries the server's facts and
  nothing else. There is no inconsistency to resolve.
- Its output type is `FormLayout`, which the consumer must already be able to construct or
  receive. It introduces no vocabulary a consumer does not already have.
- It is **opt-in and self-identifying**: a host that wants its own degraded arrangement simply
  never calls `For`, and one that uses it can say so via `IsFallback`.
- The reason to publish is the defect 0004 spent a whole ticket killing: a host that degrades
  by keeping its own field list has a second implementation of *which fields do we show*.
  Making every host re-derive that is how the defect comes back, once per consumer.

### 4. Twig does NOT ship a field-definition corpus

0003 left this open: without `fieldDefinitions`, an absent non-core field reports *not carried
by Twig* rather than *empty on the server*.

**The parameter stays optional, Twig ships no baked corpus, and the degraded answer stays
`NotCarriedByTwig`.**

`FieldDefinition` is cached **per-organization, per-process** server metadata. Baking a corpus
into a dependency-free package means shipping one org's process customization to every
consumer, where it is wrong for most of them and silently stale for the rest — and the failure
would present as a *confidently wrong* field state, which is worse than the honest one.

The current behaviour is already the right one and becomes contract: **absent metadata degrades
to "I don't know", never to a false blank.** A host with access passes what it has; a host
without gets a truthful third state. `samples/Twig.DetailHost` passes definitions and `Twig.Tui`
does not, so both paths ship exercised.

**Documentation obligation:** `Project`'s XML doc must state that omitting `fieldDefinitions`
collapses *empty on the server* into *not carried by Twig* for non-core fields. A host that
does not know this will read the third state as a Twig bug.

### 5. The conflict report is a narrow, layout-free carrier — not `WorkItemDetailDocument`

0005 §6 called this the largest unspecified surface remaining: the report carries remote values,
so it is document-shaped — does it reuse the document?

**No. A narrow field-keyed carrier.** Three reasons, in order of weight:

1. **A `WorkItemDetailDocument` cannot be built without a `FormLayout`.** A conflict happens at
   save time, in a sink, which has no layout and no reason to acquire one. Reusing the document
   would make the *error* path require an ADO round trip that the *success* path does not.
2. **A conflict concerns the fields in collision.** A form has hundreds of controls; a collision
   has one to three. Shipping the whole form to report three fields is the wrong ratio, and the
   host then has to diff two documents to find what actually collided.
3. It stays honest about what it knows: the remote *values*, not the remote *arrangement*.

Shape, per collided field: the field reference name, the value the caller started from, the
value the caller proposed, and the remote value now — plus the remote `Revision` at the top.
That is a superset of `FieldChange(FieldName, OldValue, NewValue)` with the remote value added,
which is exactly the fourth fact a resolver needs and the only one Twig does not already carry.

🔴 **The revision is the concurrency check; the prior value is not** (0005 §5 stands). The
carrier holds both, and its documentation must say which one is load-bearing, or an
implementer will compare values and ship last-write-wins.

A host that wants this document-shaped can project it itself — it has the layout and we do not.

### 6. The two obligations 0005 imposed

**Two sinks, both exercised** (0005 §7 acceptance criterion). Concretely:

- **Sink A:** Twig's `IPendingChangeStore`-backed sink, in `Twig.Tui`, over SQLite. Declares
  `System.Title`, `System.State`, `System.AssignedTo` — and `WorkItemFormView.EditableFieldRefs`
  becomes a *consequence* of that declaration rather than a constant (0005 §1).
- **Sink B:** an in-memory sink in `samples/Twig.DetailHost`, declaring a **deliberately
  different** field set, with **no** `Twig.Infrastructure`, no SQLite, no DI.

🔴 **B's field set must differ from A's, and the sample must assert the difference is
observable** — that the editable control set changed because the sink changed. Two sinks that
declare the same fields prove the interface compiles, not that the seam carries the decision.
This is the same negative-control discipline 0003 used for the boundary (`CS0122` on
`IFormLayoutProvider`) and 0004 used for the field list (reintroduce a hard-coded row, watch
7 of 16 arms fail): the check must be able to fail.

**The advisory-transition caveat is a documentation obligation** (0005 §3). Twig infers legal
transitions from standard process templates because the real per-process graph needs
process-admin permission (`StateTransitionExecutor`'s own remarks). The offered state list is
**advisory; the server is final**. This must appear in the XML doc of the capability member
that returns it — not only in this ticket — because the host that mistakes it for a guarantee
is reading IntelliSense, not the wayfinder map.

### 7. Sequenced implementation plan

Each milestone is independently shippable and independently green. `M1` is already done and is
listed so the sequence reads as one chain.

| # | Milestone | Delivered by | Gate |
|---|---|---|---|
| **M1** | Read-only projection + external host + TUI migration | **shipped** (0003, 0004) | green at `4969bf45`, above |
| **M2** | Editing capability, change proposal, conflict carrier — types + pure logic, no sink | new `Twig.Domain.Projections` types | Domain tests; red-before-green per §8 |
| **M3** | Sink A — `IPendingChangeStore` sink; `EditableFieldRefs` derived, not constant | `Twig.Tui` | `Twig.Tui.Tests` **run separately** (§9) |
| **M4** | Sink B — in-memory sink in the sample, different field set, difference asserted | `samples/Twig.DetailHost` | sample exits non-zero if the difference stops being observable |
| **M5** | State transitions — offer-time filter + entry-time re-validation, both advisory | Domain, over `ProcessConfiguration`/`StateTransitionService` | a test proving an ignored offer list is still refused at entry |
| **M6** | Release — `Unshipped` → `Shipped`, XML-doc obligations landed, package published | manifests + docs | §10's final gate |

**Ordering is load-bearing in one place: M4 before M5.** M5 adds behaviour to a seam whose
second implementation does not exist yet; building it against one sink is how the seam decays
into "Twig's store, plus an interface nobody uses". Everything else may be reordered.

### 8. Test posture — what counts as evidence here

The bar this map has already met three times, and the builder inherits:

- **Red before green, behaviourally.** A test that does not compile at the pre-fix SHA is
  honest but weak evidence (0004 §6). Extract a probe that depends on no new API and run it in
  a detached worktree at the pre-fix SHA, or reintroduce the defect on the fixed code and watch
  the suite fail. `git worktree add --detach ../twig-baseline <pre-fix-sha>`.
- **The check must be able to fail.** Assertions that a boundary holds are worth nothing
  without a negative control. Three exist to copy: `CS0122` on `IFormLayoutProvider`, the
  reintroduced hard-coded row, and the sample's non-zero exit.
- **Fixtures must not degrade into the happy path.** Where a fixture has a precondition, assert
  it. `AGENTS.md` records the `ConflictResolver.Resolve` short-circuit as the worked example —
  a conflict test whose remote revision was never advanced silently tests `NoConflict`. **M2 and
  M5 are squarely in that trap's blast radius.**
- **`MergeResult` is a `union`** — pattern-match the case (`result is HasConflicts`);
  `ShouldBeOfType<HasConflicts>()` fails against the wrapper.

**Fixture corpus.** `samples/Twig.DetailHost/Fixture.cs` is the reference corpus and already
covers 0002 §11's floor: all three field states, a non-`custom` page, a contribution control,
a contribution *group*, the core-field hole (`System.Title` absent from `Fields`), a
process-specific field, and a 300-char long value. **Extend it; do not start a second one.**
M2–M5 add: a field the sink declares, a field it does not, a state with a legal and an illegal
target, and a collided field with a remote value.

### 9. Toolchain — non-negotiable, and it has already bitten this map twice

```bash
export DOTNET_ROOT=/home/polyphonyrequiem/.dotnet-p5
export PATH=$DOTNET_ROOT:$PATH
```

- Without both, **every suite exits 145** and `tools/run-tests.sh` prints four `FAILED`
  verdicts that are not real failures. Background shells do not inherit the export — pass it
  inline.
- **Verdict only via `tools/run-tests.sh`, grep `TWIG-VERDICT`.** Never grep `Passed!`; an
  aborted run prints a clean-looking false green (`AGENTS.md`, "Reading test results").
- 🔴 **`run-tests.sh` knows four suites and `Tui` is NOT one of them.** M3 touches
  `src/Twig.Tui`, so `dotnet test tests/Twig.Tui.Tests/Twig.Tui.Tests.csproj` runs separately.
  CI's bare `dotnet test --settings test.runsettings` covers it; a local green does not.
- The sample needs `DOTNET_ROLL_FORWARD=Major` on this box — it targets GA `net10.0` and only
  an 11.0 runtime is installed here. Environmental, not a defect; it is also the whole point of
  the multi-target being exercised rather than asserted.

### 10. The final gate

The gate traces the production chain end to end and **rejects a test that substitutes any link
under review**:

```
consumer project  →  public projection  →  host-owned renderer
(samples/Twig.DetailHost, one ProjectReference)
                     (WorkItemDetailProjector.Project, real API, no injected substitute)
                                            (HostPane, caller-owned frame/scroll/selection)
```

It passes when **all** of these hold at one commit:

1. `tools/run-tests.sh` → `TWIG-VERDICT OVERALL: PASSED`.
2. `dotnet test tests/Twig.Tui.Tests/Twig.Tui.Tests.csproj` → exit 0.
3. `dotnet build` over the whole solution with `TreatWarningsAsErrors=true` → exit 0.
4. `dotnet run --project samples/Twig.DetailHost` → exit **0**, and exits **non-zero** if the
   0002 §11 acceptance floor or §6's two-sink difference stops holding.
5. `PublicAPI.Unshipped.txt` is **empty** and every entry has moved to `Shipped.txt`.
6. The three documentation obligations are in XML docs, not only in this map: §4's
   field-definition degradation, §5's revision-is-the-check, §6's advisory transitions.

🔴 **Why a test project cannot be the gate** (0003 §1, and it is the trap this whole map exists
to avoid): `Twig.Domain.csproj:44-57` grants `InternalsVisibleTo` to every first-party test
assembly. A test compiles against `internal` types and **passes while proving nothing** about an
external consumer. The sample is outside that grant, which is why the evidence lives there and
why its location is load-bearing. **Do not "tidy" `samples/` into `tests/`.**

### 11. Explicit non-goals

Carried from the map's Out of scope, plus what this ticket adds:

- A shared renderer — Terminal.Gui, Spectre.Console, ratatui, or any other.
- Node-link graph layout in a terminal. Cut 2026-08-07. Trees and lists only.
- Any host's frame, focus, keyboard routing, dimensions, scrolling, navigation, or lifecycle.
- Making Bonsai a second ADO client, or exposing Twig authentication/infrastructure to consumers.
- Rebuilding the Twig TUI session model.
- **Sibling projections** — hierarchy tree and relationships. Charted separately; do not widen
  the document, which is form-shaped.
- **A second package** (§1), **a shipped field-definition corpus** (§4), **a document-shaped
  conflict report** (§5), and **`ReadOnly`-as-editing-authority** (0002 §6, 0005 §1). Each was
  considered on the evidence and refused; reopening one needs a new fact, not a fresh opinion.

### 12. What the implementation handoff consists of

A builder picks these up, in this order:

1. **`wayfinder-detail-projection/map.md`** — destination, governing rule, and the full
   decision ledger.
2. **This ticket (0006)** — the build-ready specification: packaging, compatibility, the four
   settled open calls, the milestone sequence, the test posture, and the final gate.
3. **Ticket 0005** — the editing contract in full. M2–M5 implement it; §§1–7 are its clauses.
4. **Tickets 0001–0004** — the *why* behind anything above that looks arbitrary. 0002 for the
   document contract, 0003 for the boundary evidence and the call chain, 0004 for the migration
   and the fallback.
5. **The running code** — `src/Twig.Domain/Projections/` (three files),
   `samples/Twig.DetailHost/` (the corpus and the negative control),
   `tests/Twig.Domain.Tests/Projections/`, `tests/Twig.Tui.Tests/WorkItemFormViewDocumentWalkTests.cs`.
6. **`AGENTS.md`** before running anything, and **`CONTEXT.md`** before naming anything.

The read-only half is **built and green**, not specified. What remains to build is the editing
half (M2–M5) and the release (M6).
