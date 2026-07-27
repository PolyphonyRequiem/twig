---
id: 0016
title: AddConnectionServices and MCP child providers
type: task
status: open
blocked_by: [0004]
---

## Question

Implement the follow-on named by 0007. Move the surface-neutral service registrations into the
shared Infrastructure module, have MCP build one provider per `Connection`, and then delete
`WorkspaceContextFactory.CreateContext` and `WorkspaceContext` outright.

0007 ruled that three composition roots are correct and that the shared module already exists.
The defect is **where the seam is drawn**, not how many roots there are. This ticket moves the
seam.

*(All `file:line` citations below are as-of `0d0b1cce`, the baseline 0007 was written against.
Wayfinder 0014 was in flight at the time and touches several of these files — re-verify before
editing.)*

## Scope

**1. Move the surface-neutral registrations.**

Out of `src/Twig/DependencyInjection/CommandServiceModule.cs` (CLI-only, which MCP cannot
reference per `src/Twig.Mcp/Twig.Mcp.csproj:29-30`) and into the shared Infrastructure module:

- `ActiveItemResolver` (`:58`)
- `ProtectedCacheWriter` (`:63`)
- `SyncCoordinatorFactory` (`:69`) and its `.ReadWrite` projection (`:83`)
- `WorkingSetService` (`:86`)
- `ParentStatePropagationService` (`:117`)
- `StateTransitionWorkflow` (`:124`)
- `FieldUpdateWorkflow` (`:133`)
- `NoteWorkflow` (`:139`)
- `DiscardWorkflow` (`:145`)
- `DeleteWorkflow` (`:150`)
- `PatchWorkflow` (`:157`)
- `RefreshOrchestrator` (`:163`)
- `ContextChangeService` (`:175`)
- `IPendingChangeFlusher` (`:183`)
- `BacklogOrderer` (`:95`), `SeedPublishOrchestrator` (`:98`), `SeedReconcileOrchestrator` (`:108`)

**Membership test: does it name a surface?** A workflow does not. What stays in the CLI:
`HintEngine` (`:34`), `IEditorLauncher` / `IConsoleInput` (`:54-55`), `CommandContext` (`:191`),
`StatusFieldConfigReader` (`:199`), and everything in `RenderingServiceModule` /
`CommandRegistrationModule`.

**The stated blocker for this move is stale and must be deleted with it.**
`CommandServiceModule.cs:22-25` claims these services live in the CLI because they depend on
`IAdoWorkItemService`, *"registered with CLI-layer factory logic (DD-12)"*. It is registered at
`src/Twig.Infrastructure/DependencyInjection/NetworkServiceModule.cs:41` — in Infrastructure.
So is `IIterationService` (`:82`). Nothing in the moved block needs the CLI project. Remove the
DD-12 comment rather than carrying it across.

**2. Rename to 0004 vocabulary.**

`TwigServiceRegistration.AddTwigCoreServices` (`src/Twig.Infrastructure/TwigServiceRegistration.cs:48`)
becomes **`AddConnectionServices`**. Update its three call sites: CLI (`src/Twig/Program.cs:53`),
TUI (`src/Twig.Tui/Program.cs:58`), and `AddTwigInfrastructure` (`TwigServiceRegistration.cs:181`).
`WorkspaceKey` becomes **`Connection`** per 0004; `Workspace` is retired vocabulary.

**3. MCP resolves per-`Connection` from its own provider.**

Replace the hand-built bundle with **N sibling `ServiceCollection`s** — one per `Connection`,
each built by calling `AddConnectionServices(config, connectionTwigDir)` with that Connection's
arguments.

**This must NOT be `IServiceProvider.CreateScope`.** `AddConnectionServices` takes `twigDir` and
`startDir` as *closure* parameters, captured at `TwigServiceRegistration.cs:54` before any
provider exists. A child scope inherits the parent's registrations — closures included — and
would therefore resolve the wrong path for every Connection but the first. Sibling collections,
not scopes.

Process-wide singletons that must genuinely be shared (`HttpClient`, `IAuthenticationProvider`,
`AdoConcurrencyThrottle`) are injected as pre-built instances, exactly as the mirror already
shares them today (`src/Twig.Mcp/Services/WorkspaceContextFactory.cs:39-40`, `:100-101`).

Everything that varies per Connection varies through **one** input: `TwigConfiguration`.
`CreateContext` gets it once at `:82` and derives `TwigPaths` (`:83`), the DB path, the ADO
org/project (`:100-107`) and the team (`:109-117`) from it; `AddConnectionServices` derives the
same things from the same singleton (`TwigServiceRegistration.cs:73-77`, `:81-89`). One override
per provider is sufficient.

Keep the existing `ConcurrentDictionary<Connection, Lazy<T>>` caching shape
(`WorkspaceContextFactory.cs:42`, `:71-78`) and the disposal chain (`:265-279`) — a
`ServiceProvider` is itself `IDisposable` and disposes its own singletons, so
`SqliteCacheStore` disposal (`WorkspaceContext.cs:192`) is preserved for free.

**4. Delete the mirror.**

- `WorkspaceContextFactory.CreateContext` — 183 lines (`:80-263`).
- `WorkspaceContext` — 33 members, 33 constructor parameters
  (`src/Twig.Mcp/Services/WorkspaceContext.cs:19-193`).

Both go entirely. MCP tool classes resolve what they need from the Connection's provider.

## Why this is blocked on 0004

0004 makes reconciliation a named module, which absorbs `SyncCoordinatorFactory`,
`ProtectedCacheWriter`, `RefreshOrchestrator` and `McpPendingChangeFlusher` into one dependency.
Landing 0016 first would move four registrations that 0004 then immediately restructures. Wait.

## What this closes

**#269 and #270, by construction.** 0007 established the mechanism:
`WorkspaceContextFactory.cs:32-34` admits the factory *"mirror[s] the wiring in
`TwigServiceRegistration` + `NetworkServiceModule` + `Program.cs` but instantiating directly
rather than via DI"* — and a mirror has no compiler forcing the two copies to agree, so a
dependency added on one side is simply absent on the other. Compare
`WorkspaceContextFactory.cs:123-130` against `CommandServiceModule.cs:69-82` for one instance.
Delete the mirror and the drift is unexpressible.

This is the third appearance of twig's characteristic bug — after 0003's five call sites each
remembering a preamble, and 0004's five independent staleness opinions.

Also disappears for free: the live `SeedMutationProvider` **double registration**
(`TwigServiceRegistration.cs:129` **and** `CommandServiceModule.cs:114`). Harmless today —
last-wins on the same concrete type — but it is the tell that the boundary sits where nobody
can see both sides at once. Do not file it separately; it resolves when the seam moves.

## Explicitly out of scope

- **MCP stays frozen (0012).** No new tools, no changed tool behaviour, no CLI↔MCP parity work.
  This changes only *how* MCP obtains the services behind tools that already exist.
- **Shrinking `WorkspaceContext` is not the goal and must not be attempted as a half-step.**
  0007 ruled that 0002 + 0004 roughly halve its member count but cannot dissolve it, because
  ~15 mutation workflows survive by design — a thin accessor over 15 things is still 15 things.
  Its members are not a design; they are the mirror's output. It is deleted, not slimmed.
- Collapsing the three composition roots. 0007 ruled three is correct and the TUI ships as its
  own product.

## Verification

- The four test projects, run **serially, one per invocation, with `-m:1`**, capturing the exit
  code (see `AGENTS.md`). Baseline at `0d0b1cce` was 7,389 passing, exit 0.
- A registration-completeness check is the natural regression here — see 0008, which may already
  own the mechanism. Whatever form it takes, it must **fail on the unfixed code**: a test that
  passes before and after proves nothing.
- Confirm the MCP surface is behaviourally unchanged: same tools, same outputs, N Connections
  still isolated from one another.

## Answer

<!-- empty until resolved -->
