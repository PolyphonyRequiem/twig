---
id: 0007
title: Single composition root
type: grilling
status: closed
blocked_by: [0002]
---

## Question

Can the three composition roots become one shared registration module? The audit found the obstacle is CARDINALITY, not DI-versus-manual: the CLI has one workspace per process, MCP has N keyed by `WorkspaceKey`, and the TUI has its own third root. Proposed shape: one shared `AddWorkspaceServices(IServiceCollection)` called into the CLI root container and into a per-`WorkspaceKey` child provider in MCP, with `WorkspaceContext` shrinking from a 33-parameter bundle to a thin accessor. Deletion test on `WorkspaceContextFactory.CreateContext`: FAIL — it is a manual mirror of CLI DI, as its own doc comment admits. That mirror is the mechanism behind #269 and #270.

## Update (0002, 2026-07-26)

The owner placed the TUI *conceptually* with the CLI — *"I think of the TUI as a CLI
concept. It can be its own product though."* Same user, same terminal, same mental model;
**packaging left open**.

That does not by itself resolve the third composition root. If the TUI ships as its own
product, a separate root may be justified; if it is a mode of one binary, it is
duplication. The cardinality argument is unchanged for MCP (N per process, keyed) and now
**depends on the packaging decision for the TUI** — so this ticket cannot fully resolve
before 0002 settles that.

Note also
should be `AddConnectionServices`, and `WorkspaceKey` becomes `Connection`.

## Answer

**Three composition roots stay. The ticket's premise — "can they become one?" — is the wrong
question, because the shared registration module it proposes to create ALREADY EXISTS and all
three surfaces already reach it. What 0007 is actually asking is why `WorkspaceContextFactory`
hand-mirrors CLI DI instead of calling it. That is the whole ticket.**

*(Line numbers are as-of `0d0b1cce`. Wayfinder 0014 (StagedIdentity) was in flight in a sibling
session while this was written and touches `TwigServiceRegistration.cs`,
`WorkspaceContextFactory.cs`, `WorkspaceContext.cs` and `src/Twig.Mcp/Program.cs`. This ticket
is docs-only and changed no source; cite these lines against `0d0b1cce`, not against a later
`main`.)*

### 1. How many roots, and what goes in the shared module

**Three roots — CLI, MCP, TUI — and that is correct, not duplication.**

The TUI ruling (owner, this session: *ships as its own product, installable and invokable from
twig as `twig tui ...`*) makes the third root justified. But it was never the interesting one:
the TUI root is nine lines, and it is already the *good* case. `src/Twig.Tui/Program.cs:56-63`
builds a `ServiceCollection`, calls **`services.AddTwigCoreServices(config, twigDir)`** under
the comment *"Build DI container using shared registration"*, and resolves four services from
it. `src/Twig.Tui/Twig.Tui.csproj:22-23` references only `Twig.Domain` and `Twig.Infrastructure`
— it does not reference the CLI, so it cannot inherit CLI wiring even in principle. A separate
root plus a shared module is exactly the shape you want, and the TUI is already in it.

So the ticket's proposed `AddWorkspaceServices` is not a thing to build. It exists as
`TwigServiceRegistration.AddTwigCoreServices` (`src/Twig.Infrastructure/TwigServiceRegistration.cs:48`),
whose own doc comment says *"Shared by both CLI and TUI entry points to eliminate duplicate DI
setup"* (`:19`), paired with `NetworkServiceModule.AddTwigNetworkServices`
(`src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs:24`). The CLI calls both
(`src/Twig/Program.cs:53`, `:77`). The 0004 rename lands on these: `AddTwigCoreServices` →
**`AddConnectionServices`**.

**The seam is in the wrong place, though, and that is the real finding.** The shared module
stops at repositories, stores and a handful of domain services. Everything above it — the
workflows and orchestrators — lives in `CommandServiceModule`
(`src/Twig/DependencyInjection/CommandServiceModule.cs`), **inside the CLI project**, which MCP
cannot reference (`src/Twig.Mcp/Twig.Mcp.csproj:29-30` references only Domain + Infrastructure,
the same two as the TUI). That file registers 24 services, and 17 of them are the exact
services MCP has to rebuild by hand: `ActiveItemResolver` (`:58`), `ProtectedCacheWriter`
(`:63`), `SyncCoordinatorFactory` (`:69`), `WorkingSetService` (`:86`),
`ParentStatePropagationService` (`:117`), `StateTransitionWorkflow` (`:124`),
`FieldUpdateWorkflow` (`:133`), `NoteWorkflow` (`:139`), `DiscardWorkflow` (`:145`),
`DeleteWorkflow` (`:150`), `PatchWorkflow` (`:157`), `ContextChangeService` (`:175`).

The reason those sit in the CLI is written down at `CommandServiceModule.cs:22-25`: they
*"depend on `IAdoWorkItemService` which is registered with CLI-layer factory logic (DD-12)"*.
**That justification is stale.** `IAdoWorkItemService` is registered in
`NetworkServiceModule.cs:41` — in Infrastructure, not the CLI. So is `IIterationService`
(`:82`). Nothing in that block needs the CLI project. The comment describes a layering that no
longer exists, and it is the sole documented reason MCP duplicates seventeen services.

**Ruling on module contents:**

- **Shared (`AddConnectionServices`, Infrastructure)** — everything currently in
  `AddTwigCoreServices` + `AddTwigNetworkServices`, **plus** the 17 surface-neutral domain
  services now stranded in `CommandServiceModule`. Test for membership: *does it name a
  surface?* A workflow does not. A `SyncCoordinatorFactory` does not.
- **Per-surface** — only what genuinely names a surface: CLI keeps `HintEngine` (`:34`),
  `IEditorLauncher`/`IConsoleInput` (`:54-55`), `CommandContext` (`:191`), rendering and command
  registration; MCP keeps its tool classes and dispatcher (`src/Twig.Mcp/Program.cs:59-75`); TUI
  keeps its views.

One duplication is already visible from this seam being wrong: `SeedMutationProvider` is
registered **twice**, at `TwigServiceRegistration.cs:129` and again at
`CommandServiceModule.cs:114`. Harmless today (last-wins on the same concrete type), but it is
the tell — the boundary is drawn where nobody can see both sides at once.

### 2. `WorkspaceContext` — the god object, and what dissolves it

**Re-counted properly at `0d0b1cce`: 32 `public` properties + 1 `internal` (`CacheStore`,
`src/Twig.Mcp/Services/WorkspaceContext.cs:78`) = 33 members, and 33 constructor parameters
(`:80-113`).** The audit's "34 public properties" is wrong by one; the brief's "35 public lines"
counted the class declaration (`:19`), the constructor (`:80`) and `Dispose` (`:190`) alongside
the 32 properties. The **33-parameter** figure in the ticket's own framing is exact.

**It is not a god object with a design behind it. It is a hand-copied DI container.** The
factory's own doc comment concedes this at `WorkspaceContextFactory.cs:32-34`: *"mirroring the
wiring in `TwigServiceRegistration` + `NetworkServiceModule` + `Program.cs` but instantiating
directly rather than via DI."* `CreateContext` (`:80-263`) is 183 lines of `new`, and every
line has a counterpart in `CommandServiceModule` — compare `SyncCoordinatorFactory` at
`WorkspaceContextFactory.cs:123-130` against `CommandServiceModule.cs:69-82`, or
`StateTransitionWorkflow` at `:163-170` against `:124-131`. Two copies of one wiring graph, in
two projects, kept in sync by nothing.

The deletion test the ticket ran on `CreateContext` — **FAIL, it is a manual mirror** — is
confirmed, and the mechanism behind #269/#270 is now precise: a mirror has no compiler forcing
the copies to agree, so a dependency added on one side is simply absent on the other. That is
the same failure shape 0003 named (five call sites each remembering a preamble) and 0004 named
again (five independent staleness opinions). Third instance. It is twig's characteristic bug.

**Does 0002's read-workflow ruling dissolve it? Partly — and less than hoped.** 0002 gives reads
one method, which collapses the read-side surface; 0004 makes reconciliation a named module,
which should absorb `SyncCoordinatorFactory`, `ProtectedCacheWriter`, `RefreshOrchestrator` and
`McpPendingChangeFlusher` (`WorkspaceContext.cs:32`, `:35`, and the factory's `:120`, `:151`)
into one dependency. Those two together plausibly halve the member count. But they do not
dissolve it, because **the remaining ~15 are mutation workflows, and 0002 explicitly keeps
those as separate `Validate`/`Execute` pairs.** A thin accessor over 15 workflows is still 15
things.

**So: `WorkspaceContext` does not need its own ticket, and shrinking it is not the fix.** The
fix is that MCP resolves from a **child `IServiceProvider` per `Connection`** rather than
holding a bundle at all — at which point the bundle has nothing to be. Its members are not a
design; they are the mirror's output. Delete the mirror and the count question stops being
asked. **Follow-on ticket (naming it, not building it): "MCP resolves per-Connection from a
child provider" — move the 17 surface-neutral registrations from `CommandServiceModule` into
`AddConnectionServices`, delete `WorkspaceContextFactory.CreateContext`, and delete
`WorkspaceContext`.** It is `type: task`, it depends on 0004 landing first (so the
reconciliation module is there to be registered), and it must not start before then.

### 3. Does child-provider-per-`Connection` actually work?

**Yes — and the code already proves the hard part, though there is one real constraint the
ticket's framing missed.**

The cardinality obstacle is confirmed and correctly stated: CLI has one Connection per process
(`Program.cs:53` builds one root container from one config), MCP has N, keyed —
`ConcurrentDictionary<WorkspaceKey, Lazy<WorkspaceContext>>` at
`WorkspaceContextFactory.cs:42`, resolved through `GetOrCreate` (`:71-78`). Under 0004 vocabulary
that key is **`Connection`**.

What makes the child-provider shape work is that everything varying per Connection already
varies through **one** input: `TwigConfiguration`. `CreateContext` gets it once
(`:82`, `_registry.GetConfig(key)`) and derives everything else — `TwigPaths` (`:83`), the DB
path, the org/project on `AdoRestClient` (`:100-107`), the team on `AdoIterationService`
(`:109-117`). `AddTwigCoreServices` derives the same things from the same singleton
(`TwigServiceRegistration.cs:73-77`, `:81-89`). So a child provider needs exactly one override —
register the Connection's `TwigConfiguration` — and every downstream factory registration
recomputes correctly. This is not hand-waving; it is what the mirror is already doing, one
`new` at a time.

The constraint the ticket's framing missed: **`AddTwigCoreServices` takes `twigDir` and
`startDir` as closure parameters, not from DI** (`TwigServiceRegistration.cs:48-54` — note
`resolvedTwigDir` is captured at `:54`, before any provider exists). A child provider built by
`CreateScope` inherits the parent's *registrations*, closures included, so it would inherit the
wrong path. The mechanism is therefore **a separate `ServiceCollection` per Connection**, built
by calling `AddConnectionServices(config, connectionTwigDir)` with that Connection's arguments —
N sibling providers, not N scopes of one. Process-wide singletons that must genuinely be shared
(`HttpClient`, `IAuthenticationProvider`, `AdoConcurrencyThrottle`) get injected as instances,
precisely as the mirror shares them today (`WorkspaceContextFactory.cs:39-40`, `:100-101`). The
`Lazy<T>` + `ConcurrentDictionary` caching and the `IDisposable` chain (`:265-279`, which
disposes each context and thus each `SqliteCacheStore` at `WorkspaceContext.cs:192`) carry over
unchanged — a `ServiceProvider` is itself `IDisposable` and disposes its singletons.

Net: **N providers, one registration module, one config input each.** The shape works. Which is
the strongest possible evidence that the mirror should not exist: it is already doing this, by
hand, without the compiler's help.

### What this ticket does NOT do

Docs-only, per scope. No source file was touched. Three follow-ons are named above and in
`map.md`; none is built here. The `SeedMutationProvider` double-registration is recorded, not
fixed. MCP remains frozen per 0012 — the follow-on adds no tools and changes no tool behaviour;
it changes only how MCP obtains the services behind existing tools.
