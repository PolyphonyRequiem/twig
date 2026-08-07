---
id: 0001
title: Locate the public package boundary and reusable existing contract
type: research
status: closed
claimed_by: detail-boundary
blocked_by: []
---

## Question

Which existing Twig assemblies and types can own the public detail projection without importing Infrastructure or a UI framework, and what must change versus remain internal? Ground the answer in project references, package metadata, public API manifests, `FormLayout`, `IFormLayoutProvider`, `ProcessLayoutCommand`, `WorkItem`, appearance types, and current external package conventions.

The answer must include the real consumer construction chain and identify any place where a proposed package boundary would force a consumer to reference Twig.Infrastructure.

## Answer

Pinned at commit `173d1673627f226eb924915ebcc6db39ac6ee95a` (`origin/main`, "Merge #155:
chart hostable detail projection"), working tree clean. Every current-code claim below was
read at that commit.

### 1. The projection belongs in `Twig.Domain`

`src/Twig.Domain/Twig.Domain.csproj` is the only assembly that already satisfies every
constraint the map imposes, and it satisfies them today rather than after a refactor:

| Requirement | `Twig.Domain` | `Twig.RenderTree` | `Twig.Infrastructure` |
|---|---|---|---|
| Already packable | `PackageId PolyphonyRequiem.Twig.Domain` | `PolyphonyRequiem.Twig.RenderTree` | `PolyphonyRequiem.Twig.Infrastructure` |
| `ProjectReference` count | **0** | **0** | 1 (`Twig.Domain`) |
| Runtime `PackageReference` count | **0** (SourceLink + PublicApiAnalyzers are `PrivateAssets="All"`) | **0** (same) | 4 — `Markdig`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.DependencyInjection`, `SQLitePCLRaw.bundle_e_sqlite3` |
| Multi-targeted for consumers | `net10.0;net11.0` | `net10.0;net11.0` | `net10.0;net11.0` |
| `IsAotCompatible` | yes | — | — |
| Owns the vocabulary in question | `FormLayout`, `WorkItem`, `WorkItemSnapshot`, `WorkItemTypeAppearance` | none of it | none of it |

`Twig.Infrastructure` is disqualified by its `PackageReference` list, not by taste: a
read-only host that referenced it would acquire SQLite (native bundle included), a Markdown
renderer, and a DI container to display a form. `Twig.RenderTree` is dependency-clean but
carries the *wrong* vocabulary — `RenderNode.Hint`, `RenderAudience`, `Severity`,
`MarkupHelpers` are twig's CLI presentation contract, and `CONTEXT.md` §9 records that only
`src/Twig` consumes it. Routing the detail document through it would hand external hosts a
second package plus a renderer opinion the map puts out of scope.

`Twig.Domain` already runs the discipline this work needs: `Microsoft.CodeAnalysis.PublicApiAnalyzers`
with `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, plus a TFM-conditional
`PublicApi/net10.0/PublicAPI.Shipped.txt` carrying only the `IUnion`/`UnionAttribute`
polyfill. Making the layout types public is therefore a *tracked, reviewable* manifest diff
rather than an invisible surface change.

**The known cost, stated rather than hidden:** `Twig.Domain`'s public surface is broad —
`IWorkItemRepository`, `IUnitOfWork`, `ITransaction`, `IPendingChangeStore`,
`ISeedPublishRulesProvider` and ~30 other interfaces in `src/Twig.Domain/Interfaces/`. A
Bonsai-style consumer gets all of it in IntelliSense. That is *surface bloat, not dependency
contamination* — none of those interfaces drags a package with it, because Domain has zero
project and zero runtime package references. Splitting a narrower `PolyphonyRequiem.Twig.Detail`
buys a smaller surface and costs a second package, a second manifest, and a version-skew pair.
Do not pay that up front. The trigger to reconsider is concrete: if ticket 0005's editing
capability contract needs types that Domain's persistence interfaces would drag into the
consumer's mental model, revisit at 0006.

### 2. Reusable as-is versus must-change versus stays internal

**Already public in `Twig.Domain`, reuse unchanged:**

- `WorkItemSnapshot` (`ValueObjects/WorkItemSnapshot.cs:8`) — `public sealed record`,
  primitives only, `IReadOnlyDictionary<string,string?> Fields`, documented as "the boundary
  type that both ADO and SQLite mappers produce". This is the right value carrier for the
  projection input.
- `WorkItemTypeAppearance` (`ValueObjects/WorkItemTypeAppearance.cs:13`) — `public sealed
  record (string Name, string? Color, string? IconId)`. Already the Twig-owned appearance
  vocabulary the map names, already nullable-correct for types ADO omits colour on.
- `WorkItemType`, `IterationPath`, `AreaPath` — public classification value objects (§6 of
  `CONTEXT.md`).

**Must change from `internal` to `public` — this is the single required source change:**

- `FormLayout`, `LayoutPage`, `LayoutSection`, `LayoutGroup`, `LayoutControl`
  (`ValueObjects/FormLayout.cs:34,45,69,74,85`) are all `internal sealed record`. They are
  reachable today only through `InternalsVisibleTo` entries in `Twig.Domain.csproj:44-57`,
  which name first-party assemblies only (`Twig.Tui`, `twig`, `Twig.Mcp`, the test projects).
  An external consumer cannot see them at all. Their *shape* needs no change — all four ADO
  levels and `LayoutPage.AllGroups` are already correct per wayfinder-1.0 ticket 1004 — only
  their accessibility.
  `ProcessLayoutCommand.cs:32-36` records why they were kept internal: the shape was still
  under design while ticket 1003's editor did not exist. **This map is that editor's design.**
  Publishing them is the deliberate reversal of a deliberate hold, and it must land as
  `PublicAPI.Unshipped.txt` entries so the promotion is reviewable.

**Stays internal — publishing any of these is the failure mode:**

- `IFormLayoutProvider` (`Interfaces/IFormLayoutProvider.cs:13`) — the *acquisition* seam.
  Async, cancellable, network-shaped, returns `null` on an undetectable process. That is a
  Twig-side concern.
- `AdoIterationService` (`Infrastructure/Ado/AdoIterationService.cs:14`) — the sole
  implementation; `internal sealed`, constructed with `HttpClient`, `IAuthenticationProvider`,
  org/project/team.
- `AdoFormLayoutResponse` DTOs, `TwigJsonContext`, `ProcessLayoutCommand`, `WorkItemFormView`.
- `WorkItem` (`Aggregates/WorkItem.cs:12`) is *public* but is the wrong input: it carries
  `IsDirty`, `StagedIdentity`, `PendingNotes`, and `internal` mutators (`SetField`, `SetDirty`)
  whose meaning is twig's local pending-change model. Project from `WorkItemSnapshot`; leave
  `WorkItem` to twig-internal callers. (Ticket 0002 decides the exact document input; this
  ticket only rules out `WorkItem` as the boundary type.)
- `IconSet` (`ValueObjects/IconSet.cs:29`) is public but is **renderer policy, not
  projection**: its own remarks constrain glyphs to BMP PUA because Spectre.Console measures
  surrogate pairs as 0-width. Shipping it as part of the detail contract makes one terminal
  renderer's width bug part of an external consumer's API. Appearance travels as
  `WorkItemTypeAppearance` (`IconId` string); glyph choice stays with each host.

### 3. Exact dependency direction an external consumer gets

```
Bonsai (caller-owned pane)
  └── PackageReference PolyphonyRequiem.Twig.Domain   (net10.0 or net11.0)
        └── (nothing)
```

One package, leaf node, no transitive runtime dependencies, AOT-clean, and consumable on GA
`net10.0` so the consumer is not forced onto a preview SDK (the stated reason for the
multi-target, per the csproj comment referencing #315). No `Twig.Infrastructure`, no
`Terminal.Gui`, no `Spectre.Console`, no `Microsoft.Data.Sqlite`, no
`Microsoft.Extensions.DependencyInjection`.

### 4. The real consumer construction chain today

Both existing chains terminate inside twig, and neither is reachable from outside:

**CLI / layout path.**
`Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs:71-84` constructs
`AdoIterationService(HttpClient, IAuthenticationProvider, cfg.Organization, cfg.Project, team)`
and registers it as `IFormLayoutProvider`. `ProcessLayoutCommand.cs:38-65` takes that
interface, calls `GetFormLayoutAsync`, and hand-builds a `RenderTree` in
`BuildLayoutTree` (`:117-209`). So **the only route to a `FormLayout` object today runs
through Infrastructure, an `HttpClient`, and ADO authentication.**

**TUI / detail path.**
`Twig.Tui/Program.cs:56-58` builds a `ServiceCollection`, calls
`TwigServiceRegistration.AddConnectionServices(config, twigDir)` (public *only* because
Infrastructure's `InternalsVisibleTo` list omits `Twig.Tui` — see its own remarks at
`TwigServiceRegistration.cs:23-27`), resolves `IPendingChangeStore`, and at `:91` constructs
`new WorkItemFormView(pendingChangeStore)`. That view (`Views/WorkItemFormView.cs:15,28-41`)
is `internal sealed class WorkItemFormView : View` with ten hard-coded `TextField`s and a
`Dictionary<int, Dictionary<string,string>>` of saved edits. **It never touches `FormLayout`
at all** — the server-authored structure and the TUI detail pane are today entirely
disconnected. That disconnection is exactly what ticket 0004 must close.

**Consequence for the boundary:** the projection API must accept an *already-materialized*
`FormLayout` + `WorkItemSnapshot` as values. Acquisition (`IFormLayoutProvider` → ADO REST →
cache) stays behind Infrastructure and remains twig's job; Twig TUI keeps constructing it
through DI, and an external host is handed data by its own caller.

### 5. Shapes that would force Infrastructure or a UI framework onto read-only consumers

Each of these is a live temptation in the current code, not a hypothetical:

1. **Taking `IFormLayoutProvider` as the projection's input.** Its only implementation is
   `internal` to Infrastructure and requires `HttpClient` + `IAuthenticationProvider` +
   org/project config. A read-only pane would have to reference Infrastructure and
   authenticate to ADO to draw a form. It also makes the API `async` for no consumer benefit.
   → Take `FormLayout` by value.
2. **Exposing the projection as an `IServiceCollection` extension** (the shape
   `TwigServiceRegistration.AddConnectionServices` uses). That is a hard
   `Microsoft.Extensions.DependencyInjection` dependency for a consumer that only needs a
   pure function. → Plain constructor / static factory.
3. **Requiring `IPendingChangeStore` to render.** This is precisely what
   `WorkItemFormView`'s constructor does today (`Program.cs:91`). Its implementations live in
   `Twig.Infrastructure.Persistence` over SQLite. Any read-only path that needs it has
   imported a database. → Editing is an *optional capability* (ticket 0005), never a
   construction prerequisite.
4. **Using the `WorkItem` aggregate as the boundary type.** Public, but its `internal`
   mutators, `IsDirty`, and `StagedIdentity` encode twig's local persistence model; an
   external consumer would be reasoning about pending-change semantics it has no store for.
   → `WorkItemSnapshot`.
5. **Emitting `Twig.RenderTree` nodes as the detail document.** Adds a second package and
   imports `RenderAudience` / `Hint` / `Severity` — twig-CLI presentation opinions — into a
   contract whose whole point is that the host owns rendering.
6. **Shipping `IconSet` glyph tables in the contract.** Makes Spectre.Console's
   surrogate-pair width bug a permanent term of an external API.
7. **Anything typed against `Terminal.Gui.View`.** Only `src/Twig.Tui` references
   `Terminal.Gui`; keeping it there is the whole substance of "structure is shared, rendering
   is the host's".

### 6. Verification of the existing form-layout contract

Coverage exists and constrains any change: `tests/Twig.Infrastructure.Tests/Ado/AdoIterationServiceFormLayoutTests.cs`
(287 lines, 9 tests) pins column preservation, `AllGroups` ordering, control ordering by
`order` rather than array position, absent-`visible`-means-visible, `ControlType` and field
reference-name preservation, reference-name/process-id reporting, null-for-unknown-type, the
empty-layout-vs-no-layout distinction, and per-type caching.
`tests/Twig.Cli.Tests/Commands/ProcessLayoutCommandTests.cs` (345 lines) covers the CLI
surface. Promoting the layout types to `public` changes accessibility only and must leave
both suites untouched — that is the cheapest available regression signal for the change.
