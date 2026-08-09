---
id: 0004
title: Define and prove Twig TUI migration onto the shared projection
type: prototype
status: closed
claimed_by: tui-migration
blocked_by: [0002, 0003]
---

## Question

How does `WorkItemFormView` stop maintaining its hard-coded ten-field list and consume the same detail document without giving the shared module any Terminal.Gui or application-lifecycle responsibility?

The answer must separate read-only document painting from current editing behavior, preserve server order and process-specific fields, identify the fallback when layout data is absent, and include a narrow prototype or test that would fail if Twig TUI silently returned to a second field-selection implementation.

## Answer

**Migrated, and the guard is proven red-green.** Pinned at commit
`25d9f59d674b3c4fda2af43d240438e06749c813` (`origin/main`, "feat(projection): prove the
detail document in a real external host"), working tree clean except the deliberate edits
below. Every current-code claim was read at that commit.

`WorkItemFormView` no longer contains the word "Title" as a field decision. Its rows are
whatever the document has, in the order the document has them.

### 1. The shape of the migration

| Path | What changed |
|---|---|
| `src/Twig.Tui/Views/WorkItemFormView.cs` | Ten `TextField` members and eleven fixed rows deleted. `LoadWorkItem(WorkItem)` replaced by `LoadDocument(WorkItemDetailDocument, WorkItem)`, which walks pages → groups → controls and builds rows as it goes. |
| `src/Twig.Tui/DetailDocumentSource.cs` | **New.** The TUI's single acquisition seam: layout → projection → document. |
| `src/Twig.Domain/Projections/FallbackFormLayout.cs` | **New.** The absent-layout answer (§4). |
| `src/Twig.Domain/Services/WorkItemMapper.cs` | `ToSnapshot(WorkItem)` — the aggregate→snapshot direction the TUI needs to reach a projector that deliberately takes values, not aggregates. |
| `src/Twig.Tui/Program.cs` | Wires the source into the tree-selection handler. |
| `src/Twig.Domain/Twig.Domain.csproj` | `InternalsVisibleTo` for **`twig-tui`** (§7). |
| `src/Twig.Domain/PublicAPI.Unshipped.txt` | +8 entries. |

**Nothing was added to the shared module that a host owns.** `Twig.Domain` acquired one
pure function over values and one aggregate→snapshot mapper. No Terminal.Gui type, no
`IApplication`, no lifecycle, no widget vocabulary crossed the boundary — the negative
control from 0003 §3 still holds, and `dotnet build` over the solution is green with
`TreatWarningsAsErrors=true`.

### 2. How the view stops maintaining a field list

The old constructor built the form. The new constructor builds only *chrome* — a field
area, a save button, a dirty indicator, a status label. Every field row is created inside
`LoadDocument` from a `DetailControl`, and destroyed on the next load.

The three states reach the pane and are rendered differently, which the old code could not
do at all: it read `item.Fields[key]` directly (`:285` pre-fix) and therefore rendered
"empty on the server" and "not carried by Twig" identically as blank, while special-casing
the eight core fields by hand because they are absent from that dictionary entirely. That
core-field hole is now resolved by the projection reading `WorkItemSnapshot`'s properties,
not by the view knowing which fields are special.

### 3. Read-only painting vs editing — the line, and where it is NOT

Painting is read-only and takes no store: `ReadOnlyDocumentPainting_NeverTouchesThePendingChangeStore`
asserts zero calls on the substitute across a full load. The store is reached only in
`OnSave`.

**The subtle part is what remains a TUI decision.** The view still decides which rows accept
typing — but that is *editability*, not field selection, and the distinction is load-bearing:
the row exists either way because the document has it. The authority is
`WorkItemFormView.EditableFieldRefs`, the three fields `IPendingChangeStore` can actually
persist (`System.Title`, `System.State`, `System.AssignedTo` — exactly what the pre-fix
`OnSave` wrote). Widening it is **0005's** problem.

🔴 **`DetailControl.ReadOnly` is deliberately NOT the authority here.** 0002 §6 says it is
reported and never enforced, so a server-read-only Title still accepts typing in the TUI —
pinned by `LoadDocument_ServerReadOnlyIsNotEnforced_OnlyReported`. Wiring editability to
that flag would have been the natural-looking move and would have silently converted a
reporting field into an editing contract, which 0002 §11 explicitly forbids.

Dirty tracking is now per-row diffing rather than three named originals, so it extends to
whatever the editable set becomes without another edit here.

### 4. The fallback when layout data is absent — SETTLED

**`FallbackFormLayout.For(snapshot)` returns a `FormLayout`, not a field list.** That single
choice is the whole answer. A host that degrades by keeping its own hard-coded list ends up
with the two implementations this ticket exists to prevent; producing a layout means both
paths end in the same `WorkItemDetailProjector.Project` call and the view cannot tell them
apart — pinned by `GetAsync_BothBranchesReturnTheSameDocumentType` and
`LoadDocument_FallbackLayout_PaintsCoreFieldsAndIsIndistinguishableToTheView`.

The map's warning is real and shaped the arrangement: **it cannot enumerate
`WorkItemSnapshot.Fields`**, because all eight core fields are missing from that dictionary
— such a form would have no Title, State, or Assigned To. So the arrangement is the eight
core fields in a stable Twig-authored order, then every carried field in the snapshot's own
order, in one `custom` page and one group.

**Every control it emits is resolvable.** It names only fields Twig demonstrably carries, so
projecting it never yields *not carried by Twig*
(`ProjectedFallback_NeverReportsNotCarriedByTwig`). That is the honest shape: with no server
layout Twig does not know which fields the form *ought* to have, so it claims only what it
can show and invents no absent rows.

🔴 **The two absent-layout cases stay distinct, and this is the trap.** A provider returning
`null` means *no layout was served* → fallback. A served layout with **no pages** means *the
server says there are no controls* → projected as-is, producing an empty form. Collapsing
them would make an empty server form silently sprout Twig-authored rows. Pinned twice, at
both levels: `AnEmptyServedLayout_IsNotTheSameFactAsNoLayout` and
`GetAsync_EmptyServedLayout_IsNotTreatedAsNoLayout`. The parse already made this
distinction available (`AdoIterationServiceFormLayoutTests`); this consumes it rather than
discarding it.

A fallback is self-identifying (`ProcessId == "twig.fallback"`, `IsFallback(layout)`) so a
host can say "this arrangement is Twig's, not your server's" — but nothing in the pipeline
branches on it, by design.

An unreachable or erroring provider degrades to the fallback rather than blanking the pane
(`GetAsync_ProviderThrows_DegradesToTheFallback`), because "I could not ask" is the same
epistemic position as "nothing was served".

**Update the map's "Not yet specified": this entry is now closed.**

### 5. Server order and process-specific fields

Order is read, never remembered:
`LoadDocument_PreservesServerOrder_EvenWhenItContradictsTheOldFixedOrder` feeds Area →
State → Title, the reverse of the old fixed order, and asserts the pane matches. Column
merging stays a host decision — the pane walks `AllGroups` and concatenates, exactly as
0003 §6's reference host does, and `Sections` remains available.

Process-specific fields need no special handling and get none:
`LoadDocument_PaintsAProcessSpecificFieldTheOldListNeverKnew` shows
`Contoso.Compliance.ReviewTicket` painted. The pre-fix view could not display it at any
value, because it had no widget for it. This is the migration's payoff, stated as a test.

### 6. The acceptance floor — a test that fails if a second implementation returns

`tests/Twig.Tui.Tests/WorkItemFormViewDocumentWalkTests.cs`, 16 arms, two kinds:

- **Structural.** The class declares no `TextField` members; every `Load*` method takes a
  `WorkItemDetailDocument`; no field-reference-name string constants beyond the editable set.
- **Behavioural.** Painted rows equal the document's controls in order; a process-specific
  field appears; a field the document lacks has **no row**; and — the load-bearing one —
  `LoadDocument_EmptyDocument_PaintsAnEmptyForm`. If *any* row survives a document with no
  controls, a hard-coded list is still reachable.

🔴 **Red-green verified twice, because compile-failure red proves little.** The new tests do
not compile at `25d9f59d` (no `LoadDocument`, no `FallbackFormLayout`), which is honest but
weak evidence. So:

1. **Behavioural red at the pre-fix SHA.** The two structural arms were extracted into a
   probe depending on no new API, compiled in a detached worktree at `25d9f59d`, and run:

   ```
   Twig.Tui.Tests.BaselineStructuralGuardProbe.View_DeclaresNoPreBuiltFieldWidgets [FAIL]
     WorkItemFormView must not declare per-field widgets; found: _titleField, _stateField,
     _assignedToField, _iterationField, _areaField, _effortField, _priorityField,
     _tagsField, _descriptionField
   Twig.Tui.Tests.BaselineStructuralGuardProbe.View_ExposesNoPerFieldLoadEntrypoint [FAIL]
     LoadWorkItem must take the shared document.
   Failed! - Failed: 2, Passed: 0
   ```

2. **Red against a REINTRODUCED list on the migrated code** — the failure mode that
   actually matters, since the ticket's trap is regression, not history. One hard-coded
   description row was added back to the fixed view and the suite re-run: **7 of 16 arms
   failed**, including `LoadDocument_EmptyDocument_PaintsAnEmptyForm`,
   `LoadDocument_OmitsAFieldTheDocumentDoesNotCarry`, and
   `View_DeclaresNoPreBuiltFieldWidgets`. The regression was then reverted and the suite
   returned to 67/67.

### 7. One incidental defect found and fixed

`Twig.Domain.csproj` granted `InternalsVisibleTo` to **`Twig.Tui`** — but that project's
`AssemblyName` is **`twig-tui`**, so the grant never applied and the TUI could not see
`IFormLayoutProvider`. The repo already carries the dual-name pattern for `twig` and
`twig-mcp`; `twig-tui` was simply missed. It surfaced as `CS0122` the moment anything in the
TUI touched an internal domain interface, which nothing previously did.

### 8. Verification

- `tools/run-tests.sh` — **`TWIG-VERDICT OVERALL: PASSED`**; Cli 3074, Infrastructure 1416,
  Mcp 1295, Domain 1938 (+14 from `FallbackFormLayoutTests`).
- 🔴 **The runner covers four suites and `Tui` is not one of them.** Run separately:
  `Passed! - Failed: 0, Passed: 67`, exit code **0**. CI's bare `dotnet test --settings
  test.runsettings` does cover it; a local `run-tests.sh` pass does not.
- `dotnet build` over the whole solution succeeds with `TreatWarningsAsErrors=true`.
- 0003's sample still exits **0** with `PROBE OK`, so the boundary it pins did not move.
- Toolchain: `DOTNET_ROOT=~/.dotnet-p5` plus that directory on `PATH`, or every suite exits
  145 and prints four false `FAILED` verdicts. The sample additionally needs
  `DOTNET_ROLL_FORWARD=Major` on this box — it targets GA `net10.0` and only an 11.0
  runtime is installed here. That is environmental; 0003's claim is unaffected.

### 9. Consequences for downstream tickets

- **0005 (editing):** the seam is `EditableFieldRefs` and `ChangeTypeFor`, both in one
  place, and the row model already carries per-field original values so widening the set
  needs no new dirty-tracking. `DetailControl.ReadOnly` is deliberately *not* wired to
  editability — see §3 — and 0005 must decide consciously whether to change that rather
  than inherit it.
- **0006 (packaging):** the surface grew by **8** `PublicAPI.Unshipped.txt` entries
  (`FallbackFormLayout` + `WorkItemMapper.ToSnapshot`), 230 total on this map. The new
  question 0006 inherits: **is the fallback part of the public package at all?** It is
  Twig's opinion about arrangement, not the server's structure — closer in kind to
  `WorkItemTypeAppearance`, which 0002 §9 deliberately kept *outside* the document. A
  consumer might reasonably want its own degraded arrangement.
- **0006 (fan-in):** the field-definitions question 0003 raised is now sharper in one
  direction — the fallback sidesteps it by naming only carried fields, so it never needs
  metadata to avoid a false *not carried*. That does not resolve it for the served-layout
  path, where it remains open.
