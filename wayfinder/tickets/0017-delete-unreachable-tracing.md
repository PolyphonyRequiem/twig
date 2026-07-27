---
id: 0017
title: Delete the unreachable tracing machinery
type: task
status: open
blocked_by: []
---

## Question

Graduated from [Startup cost and observability](0011-startup-and-observability.md). The decision is
made — `Twig.Domain/Diagnostics/` **fails the deletion test** — and this ticket carries it out.

There is **no `ActivityListener` registered anywhere in `src/`**. A tree-wide grep for
`ActivityListener`, `OpenTelemetry`, and `AddOpenTelemetry` across `src/**/*.cs` and every
`.csproj` returns exactly one hit: a comment inside `src/Twig.Domain/Diagnostics/TwigActivitySource.cs:9`
describing what happens when no listener is registered.

By that file's own documented contract (`:9-11`) — *"When no `ActivityListener` is registered,
`ActivitySource.StartActivity` returns null and instrumentation is effectively zero-cost"* —
**every span in twig is null at runtime and every tag written onto it is discarded.** The
`TraceTags` allowlist governs data that reaches no consumer.

### Scope

Delete `src/Twig.Domain/Diagnostics/` (3 files):

- `TwigActivitySource.cs`
- `ActivityHelper.cs`
- `TraceTags.cs`

And the ~20 call sites that feed them:

- `src/Twig/Commands/CommandActivityScope.cs` — the whole type (`:33`, `:42`, `:51`), plus its
  ~18 `using var scope = new CommandActivityScope(...)` users across `src/Twig/Commands/`
- `src/Twig/Rendering/SpectreRenderer.cs:679`, `:1495`
- `src/Twig.Infrastructure/Ado/AdoRestClient.cs:293-294`, `:351`, `:356`, `:521`, `:548`, `:553`,
  `:557`, `:567`, `:573`

And the test file `tests/Twig.Domain.Tests/Diagnostics/TwigActivitySourceTests.cs`, which passes
only because it installs a listener at `:21` and `:82` that the product never installs — it
verifies the machinery against itself.

### Explicitly NOT in scope

- **`ITelemetryClient` stays.** Unlike the ActivitySource it has a live implementation, a real
  consumer path (`src/Twig/Commands/TelemetryHelper.cs` and four commands), and is opt-in dark by
  default — inert unless `TWIG_TELEMETRY_ENDPOINT` *and* `TWIG_TELEMETRY_KEY` are both set
  (`src/Twig.Infrastructure/Telemetry/TelemetryClient.cs:48-55`). It is unused-but-*reachable*,
  not unreachable. Do not extend it either.
- **Do not add OpenTelemetry.** Rejected in 0011 §4 on 0001's single-user-local-tool constraint:
  a per-invocation CLI whose whole "trace" is one ~150 ms process has nothing distributed to
  correlate, and it would put a dependency on `Twig.Domain`, which today has none, for no live
  consumer.

If a consumer ever appears, the seam to re-add is a single `ActivityListener` at the composition
root — a few lines. That is cheaper than carrying ~200 lines of unreachable machinery, which has
already cost real time: it made 0011's telemetry hypothesis look plausible for the length of an
investigation.

### Acceptance

Deletion only — no behaviour change, since none of this code has an observable effect today.
Per `AGENTS.md`, run the four test projects **serially, with `-m:1`, capturing the exit code**, and
grep for `Passed!|Failed!|Aborted|\[FAIL\]`. Trust the exit code, not the summary line.

Baseline to beat: **7,389 passing, exit 0** (Cli 2883 / Infra 1354 / Mcp 1314 / Domain 1838),
minus whatever `TwigActivitySourceTests.cs` contributes.

## Answer

<!-- empty until resolved -->
