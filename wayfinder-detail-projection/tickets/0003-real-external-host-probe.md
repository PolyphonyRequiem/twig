---
id: 0003
title: Prove the real API inside a caller-owned external pane
type: prototype
status: closed
claimed_by: host-probe
blocked_by: [0002]
---

## Question

Can an external consumer project reference the proposed public package, construct the real projection from representative work-item and form-layout data, and paint it inside a frame the caller owns—without Twig.Infrastructure, Terminal.Gui, Spectre.Console, or an injected substitute for the projection?

Build the smallest disposable probe that demonstrates nested structure, one process-specific field, one long/rich value whose full source remains accessible, appearance metadata, scrolling/selection owned by the caller, and a truthful unsupported-control treatment. Record the exact call chain and package references.

## Answer

**Yes — built, run, and green.** Pinned at commit `5b6064c3e49170babc236adfae4e2788488c3ad5`
(`origin/main`, "docs(wayfinder): settle the detail document contract"), working tree clean
except the deliberate edits below. Every current-code claim was read at that commit.

The probe is not a thought experiment: `samples/Twig.DetailHost` builds against `Twig.Domain`
alone, constructs the real projection, paints a caller-owned pane, and exits non-zero if it
ever stops proving the acceptance floor.

### 1. Where the probe lives — the open call, decided

**An in-repo sample: `samples/Twig.DetailHost`.** Not a test project, not a sibling Bonsai
spike.

| Option | Rejected because |
|---|---|
| Test project | `Twig.Domain.csproj:44-57` grants `InternalsVisibleTo` to every first-party test assembly. A test would compile against `internal` types and pass while proving nothing about an external consumer. **This is the exact trap the ticket exists to avoid**, wearing a green tick. |
| Sibling Bonsai spike | Puts the load-bearing evidence in another repo, where it does not build in this repo's CI and rots the moment the surface moves. The map's boundary claim then rests on something no PR here can break. |
| **In-repo sample** ✅ | Outside the `InternalsVisibleTo` list, so it can only see genuinely public API; inside the solution, so `dotnet build` breaks the moment the boundary regresses. |

The sample is `IsPackable=false`, has exactly one `ProjectReference` (`Twig.Domain`), and
targets **`net10.0` — GA, not the preview SDK twig itself builds on** — which is the multi-target's
stated purpose (#315) actually exercised rather than asserted.

**The location is load-bearing, not cosmetic. Do not "tidy" it into `tests/`.** The csproj
comment says so at the file, because the reason is invisible from the directory name.

### 2. The exact call chain a consumer writes

Three lines. No provider, no `await`, no DI container, no store, no authentication:

```csharp
WorkItemDetailDocument document =
    WorkItemDetailProjector.Project(layout, snapshot, fieldDefinitions); // fieldDefinitions optional
WorkItemTypeAppearance appearance = /* asked for separately */;
pane.Load(document, appearance);
```

Package references the consumer acquires, verified by
`dotnet list samples/Twig.DetailHost/Twig.DetailHost.csproj package --include-transitive`:

```
[net10.0]:
  > MinVer  7.0.0   (PrivateAssets=All, build-time only)
```

Nothing else. The build output directory contains `Twig.DetailHost.dll` and `Twig.Domain.dll`
and no third assembly. No `Twig.Infrastructure`, no `Terminal.Gui`, no `Spectre.Console`, no
`Microsoft.Data.Sqlite`, no `Microsoft.Extensions.DependencyInjection`.

### 3. The negative control — the probe genuinely cannot see internals

An assertion that a boundary holds is worth nothing without a check that would fail if it
did not. Adding one line to the sample:

```csharp
_ = typeof(Twig.Domain.Interfaces.IFormLayoutProvider);
```

```
error CS0122: 'IFormLayoutProvider' is inaccessible due to its protection level
```

So the acquisition seam is **structurally** out of reach from a consumer, not merely unused
by this one. The layout therefore had to be built from fixture data, which is what the map
required and what the sample does.

### 4. What shipped

| Path | What |
|---|---|
| `src/Twig.Domain/Projections/WorkItemDetailDocument.cs` | The document: `WorkItemDetailDocument` → `DetailPage` → `DetailSection` → `DetailGroup` → `DetailControl`, plus `DetailFieldState` (three values) and `DetailFieldValue (State, Full, Short)`. |
| `src/Twig.Domain/Projections/WorkItemDetailProjector.cs` | `static Project(FormLayout, WorkItemSnapshot, IReadOnlyDictionary<string, FieldDefinition>? = null)`. A pure function over values. |
| `src/Twig.Domain/ValueObjects/FormLayout.cs` | `internal` → `public` on all five layout records. **Shape unchanged** — accessibility only, exactly as ticket 0001 scoped it. |
| `src/Twig.Domain/PublicAPI.Unshipped.txt` | +222 entries covering both the promotion and the new projection types, so the surface change is a reviewable manifest diff. |
| `samples/Twig.DetailHost/` | The probe: `Fixture.cs`, `HostPane.cs`, `Program.cs`. |
| `tests/Twig.Domain.Tests/Projections/WorkItemDetailProjectorTests.cs` | 18 tests pinning the 0002 contract. |
| `Twig.slnx` | New `/samples/` folder, so a boundary regression breaks a bare `dotnet build`. |

### 5. The acceptance floor, met and self-enforcing

0002 §11 required all three field states, a non-`custom` page, and a contribution control.
`Program.CheckAcceptanceFloor` asserts each and returns exit code 1 on any miss, so **the
sample cannot decay into a demo that prints something pleasant** — the check travels with it.

| Floor item | How the fixture exercises it | Observed |
|---|---|---|
| **Has a value** | `System.Description`, `Microsoft.VSTS.Common.Priority` | `Priority: 2` |
| **Empty on the server** | `Microsoft.VSTS.Common.AcceptanceCriteria` — importable per its metadata, absent from `Fields` | `Acceptance Criteria: —` |
| **Not carried by Twig** | `Contoso.Compliance.SignedOff`, a **boolean** — `FieldImportFilter` refuses booleans outright | `Signed off: <not carried by twig>` |
| **Non-`custom` page** | `Links` and `History` pages, zero sections | `# Links  (server-rendered 'links' page — not shown here)` |
| **Contribution control** | `ms.vss-work-web.risk-assessment-control`, plus a whole contribution *group* | `Risk assessment: <add-in … — this pane cannot draw it>` |
| **The core-field hole** | `System.Title` — absent from `Fields` entirely | `Title: Expose a hostable work-item detail projection` |
| **Process-specific field** | `Contoso.Compliance.ReviewTicket` | `Review ticket: SEC-4471` |
| **Long value, full source preserved** | 300-char HTML `System.Description` | row shows the short form; expansion prints the untruncated value |

### 6. What the probe establishes that a unit test could not

Every one of these is a decision the host made and Twig could have stolen by baking it into
the projection:

- **The frame.** Border glyphs, 76×22 dimensions, padding — all in `HostPane`, none in Domain.
- **Column merging.** The pane is narrow so it walks `AllGroups` and concatenates. `Sections`
  is still there for a wider host. The projection took no view.
- **Which rows exist at all.** The pane drops `Visible: false` controls *as host policy*.
  The document carried them; a duplicate-review pane wanting to show hidden fields just
  wouldn't. `ReadOnly`/`Visible` are reported and never enforced.
- **Scrolling and selection.** `Scroll()` / `MoveSelection()` are the caller's, over its own
  row list.
- **Condensed vs expanded.** The row draws `Short`; pressing into it prints `Full`. Both were
  in the document, so the host chose without re-cutting the string itself.
- **Truthful unsupported-control treatment.** The pane keeps a `SupportedControlTypes` set
  and names anything else *verbatim* — `<unsupported control type 'Contoso.WeirdWidget'>`.
  This only works because §7 refused a closed widget enum; an `Other` bucket would have
  discarded the name the host prints.

### 7. Verification

- `tools/run-tests.sh` — **`TWIG-VERDICT OVERALL: PASSED`**; Cli 3074, Infrastructure 1416,
  Mcp 1295, Domain 1924.
- The nine pre-existing `AdoIterationServiceFormLayoutTests` pass untouched, which is
  ticket 0001 §6's stated regression signal for the accessibility promotion.
- `dotnet build` over the whole solution succeeds with `TreatWarningsAsErrors=true`.
- `dotnet run --project samples/Twig.DetailHost` exits **0** and prints `PROBE OK`.

🔴 **Running anything here requires `DOTNET_ROOT=~/.dotnet-p5` and that directory on `PATH`.**
Without it every suite returns exit code **145** and `run-tests.sh` reports four FAILED
verdicts that look like real test failures and are not. It bit this session once, in a
background shell that did not inherit the export.

### 8. Consequences for downstream tickets

- **0004 (TUI migration):** the target now exists and is public. `WorkItemFormView`'s ten
  hard-coded `TextField`s are replaced by a walk that `HostPane.Load` already demonstrates
  end to end — the migration has a worked reference, not just a contract.
- **0005 (editing):** unchanged and still additive. Nothing in `Project` touches
  `IPendingChangeStore`; read-only construction demonstrably never needs one.
- **0006 (packaging):** the 222 `PublicAPI.Unshipped.txt` entries are the concrete surface
  whose compatibility promise 0006 must decide. The known cost from 0001 §1 is now visible
  rather than predicted: the sample's IntelliSense carries all of Domain's ~30 interfaces
  alongside five projection types. That is still *surface bloat, not dependency
  contamination* — the transitive package list is empty — and remains 0006's call.
- **New, non-blocking:** `Project`'s optional `fieldDefinitions` parameter is what separates
  *empty on the server* from *not carried by Twig* for an absent field. Without it, an absent
  non-core field reports **not carried**, because Twig cannot honestly claim the server said
  blank. That is the conservative direction — it degrades to "I don't know" rather than to a
  false blank — but it means a host wanting the sharper distinction must pass metadata it may
  not have. Whether Twig should ship the field definitions alongside the document is a
  packaging question for 0006, not a contract change here.
