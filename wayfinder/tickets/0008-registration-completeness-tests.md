---
id: 0008
title: Registration completeness tests
type: task
status: closed
---

## Question

Add completeness tests across all six registration touch points, so a missing registration fails the build instead of silently breaking a capability. MCP has three touch points (`AddSingleton<XTools>` at `Twig.Mcp/Program.cs:65-75`, `.WithTools<X>()` at `:107-118`, `AllToolNames` at `McpToolCatalog.cs:22-65`) and the code comments the trap on itself at `Program.cs:63-64`. The CLI has three (handler method, `CommandRegistrationModule.cs:36-107`, `GroupedHelp.KnownCommands` at `Program.cs:1180-1293`) and only the third currently has a test guard. The invariant holds today (41=41, 40=40, no orphans) BY HAND ONLY. This is cheap, independent of every other ticket, and kills the whole footgun class without any refactor — which is why it has no blockers and can be taken at any time.

## Answer

Done. Nine tests now guard all six touch points, and they found a live bug.

**CLI** — `tests/Twig.Cli.Tests/DependencyInjection/CommandRegistrationCompletenessTests.cs`:
- every command a dispatcher handler resolves is registered in `CommandRegistrationModule`
- every registration is reachable from a handler, directly or transitively through
  another command's constructor (`RefreshCommand` has no handler of its own —
  `SyncCommand` delegates its pull phase to it)
- every dependency of every command's widest constructor is resolvable

**MCP** — `tests/Twig.Mcp.Tests/McpRegistrationCompletenessTests.cs`:
- every `[McpServerToolType]` has both `AddSingleton<T>()` and `.WithTools<T>()`
- those two lists are asserted to be the *same set*, which is where drift shows up
- `AllToolNames` exactly matches the declared `[McpServerTool]` methods, in both
  directions — no invisible tool, no orphaned catalog entry
- `CompactToolNames` / `BatchableToolNames` stay subsets of `AllToolNames`
- every tool's widest constructor is fully resolvable

### The constructor assertion is the load-bearing part

.NET DI selects the *greediest satisfiable* constructor. A plain "does it resolve?"
test therefore stays green while the capability silently degrades — which is exactly
how #268 and #270 shipped. Asserting that every parameter of the widest constructor
is resolvable is the only form that actually proves the intended constructor ran.

### Live bug found

`twig save` (Program.cs `Save` handler) resolved `SaveCommand`, which was **never
registered anywhere**. Every user who ran the command got an
`InvalidOperationException` at runtime. Registered it — this is precisely the footgun
class the ticket was opened to kill, and it was sitting on `main` unnoticed.

### Verification

The invariant held before this change, so passing tests would have proven nothing.
Each guard was verified by injecting a regression and confirming the specific failure:

| Injected regression | Guard that fired |
|---|---|
| remove `AddSingleton<SeedTools>()` | singleton + same-set |
| drop `twig_workspace` from `AllToolNames` | catalog match + subset |
| remove `AddSingleton<SprintCommand>()` | handler-resolved-is-registered |
| unregister optional `IAdoGitService` | widest-constructor (resolution still **succeeded** — the degraded path) |

The first injection run also exposed a weakness in the guard itself: the MCP source
scrape matched a *commented-out* registration, so a disabled registration would have
counted as present. Fixed by stripping line comments before scraping.

Every scrape-based assertion carries a lower-bound count check, so a future refactor
that breaks discovery fails loudly instead of going vacuously green.

Suite: 7,383 tests, exit 0 across all four projects (baseline 7,374 + 9).

