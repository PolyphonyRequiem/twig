---
id: 0011
title: Startup cost and observability
type: grilling
status: closed
claimed_by: session-0011
blocked_by: []
---

## Question

Twig has no latency budget, no profiling mode, and no local trace/log export. Should
observability become a first-class, cross-surface concern — and where does the startup
cost actually go?

### Measured baseline (2026-07-26, twig 0.84.3, worktree twig-251)

```
twig --help  (20 runs):  5085 5062 69 74 69 71 70 70 67 65 66 68 66 68 68 80 71 72 66 67   (ms)
twig list    ( 5 runs):    64  62 69 67 65                                                  (ms)
```

Steady state ~68ms is acceptable. The finding is the **~5.08s spike on `twig --help`** —
a command that should touch neither the network nor the cache. It recurs intermittently
(runs 1 and 4 of one batch, runs 1 and 2 of the next), consistent with a cache/expiry
boundary rather than pure cold start.

`5080ms` matches, to within noise, the only two 5-second timeouts in the tree:

- `src/Twig.Infrastructure/Auth/MsalTokenRefresher.cs:17` — `DefaultRefreshTimeout = TimeSpan.FromSeconds(5)`
- `src/Twig.Infrastructure/Telemetry/TelemetryClient.cs:53` — `new HttpClient { Timeout = TimeSpan.FromSeconds(5) }`,
  registered as a **singleton** at `src/Twig.Infrastructure/TwigServiceRegistration.cs:126`

Hypothesis to confirm or kill: a blocking network call (token refresh or telemetry POST)
runs during composition-root construction, so it is paid by *every* command including
`--help`, and its failure is invisible because a timeout swallows it. If true this is a
correctness/latency defect, not a sync-policy problem, and is independent of 0004.

### What the answer must decide

1. **Root cause of the 5s spike** — which of the two timeouts, and why it is on the
   `--help` path at all. Should the composition root be lazy so that no command pays for
   services it never resolves?
2. **A latency budget.** What is the ceiling for a local-only command (no remote fetch)?
   Without a stated number there is nothing to regress against.
3. **A profiling mode.** `--profile` / `TWIG_PROFILE=1` emitting a per-phase span
   breakdown (process start → composition root → workspace discovery → SQLite open →
   command execute → render). This is the instrument the other tickets need: 0004 and
   0005 are both arguing about work that nobody has timed.
4. **Instrumentation seam.** Twig has `ITelemetryClient` (Domain interface, App Insights
   envelope shape hardcoded in the Infrastructure impl) and no `ActivitySource`/`ILogger`
   story. Should this become OpenTelemetry — `ActivitySource` + `ILogger` in Domain,
   exporter chosen at the composition root — so that App Insights is one exporter rather
   than the only shape? Note the **three composition roots** (CLI, MCP, TUI) — see 0007:
   whatever is chosen must be registered once, or it will be registered inconsistently.
5. **Local traces to Grafana.** Owner-stated goal: local logs and traces landing in
   Grafana to answer questions over the long term. Decide the target stack (OTLP →
   Grafana Alloy/Tempo/Loki, or the all-in-one Grafana OTEL-LGTM container), whether it
   is opt-in dev-only or a supported user-facing mode, and what the privacy boundary is
   given `ITelemetryClient` today ships to App Insights.

### Relationship to the rest of the map

- **0004 (does reconciliation exist?)** — the staleness clock lives inside
  `SyncCoordinator.SyncItemAsync:51`, so "when do we sync" is a side effect of a read
  path. That is a latency *policy* question. This ticket supplies the measurements that
  argument currently lacks; it does not decide the policy.
- **0005 (persistence model)** — SQLite open cost per command is a measured input here,
  not an assumption there.
- **0007 (single composition root)** — if the 5s spike is eager service construction,
  0011 and 0007 share a fix.

## Answer

**The observability machinery should NOT become a first-class cross-surface concern, and the
5s spike is neither of the two timeouts the ticket names.** Both halves of this ticket resolve
against the assumption they were written on.

### 1. Root cause of the 5s spike — measured, not inferred

The hypothesis in the Question ("a blocking network call — token refresh or telemetry POST —
runs during composition-root construction") is **killed on three independent grounds**:

- **The composition root does not run on `--help`.** `ConsoleApp.Create().ConfigureServices(...)`
  (`src/Twig/Program.cs:33-115`) is deferred: ConsoleAppFramework 5.7.13 documents that
  `ConfigureServices` "is called after command routing is completed and just before the actual
  parameter parsing process begins" (`~/.nuget/packages/consoleappframework/5.7.13/README.md:1134`).
  The `--help` short-circuit at `src/Twig/Program.cs:128-132` returns before routing completes,
  so the DI lambda never executes. Every service in it is therefore already unpaid on `--help`.
- **Nothing in that lambda is eager anyway.** Every registration in
  `TwigServiceRegistration.AddTwigCoreServices` is `AddSingleton(sp => ...)` factory-based
  (`src/Twig.Infrastructure/TwigServiceRegistration.cs:59-138`), including
  `ITelemetryClient` at `:126`. A factory singleton constructs on first *resolution*, not on
  registration.
- **`TelemetryClient` cannot make a call at all in this environment.** It reads
  `TWIG_TELEMETRY_ENDPOINT` / `TWIG_TELEMETRY_KEY` at construction and allocates its
  `HttpClient` only when **both** are set (`src/Twig.Infrastructure/Telemetry/TelemetryClient.cs:48-55`);
  otherwise `IsEnabled` is false and `TrackEvent` returns immediately (`:76-77`). Both vars are
  unset. Even when enabled, the POST is `Task.Run` fire-and-forget (`:83-114`) — it cannot block
  a command. Likewise `MsalTokenRefresher`'s 5s timeout
  (`src/Twig.Infrastructure/Auth/MsalTokenRefresher.cs:17`) is reachable only through
  `TryRefreshAsync`, an ADO auth path with no caller on `--help`.

**The actual cause is `CompanionStartup.RunFirstRunCheck()` at `src/Twig/Program.cs:31`** — an
unconditional, blocking, network-touching call that runs **before** the `--help` short-circuit at
`:128`, before `--version` at `:123`, and before the unknown-command path at `:151`. It resolves
to `CompanionFirstRunCheck.EnsureCompanionsAsync`
(`src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs:22`), which — when a companion binary
is missing and the `.twig-version` marker does not match — performs a GitHub REST call
(`GitHubReleaseClient.GetReleaseByTagAsync`, `src/Twig.Infrastructure/GitHub/GitHubReleaseClient.cs:46-50`)
and then downloads and extracts a release archive, under a **60-second** budget
(`CompanionFirstRunCheck.cs:15`), synchronously via `.GetAwaiter().GetResult()`
(`src/Twig/Program.cs:1155-1156`).

Measured on the installed binary, twig 0.84.3 (`~/.local/bin/twig.exe`), this session:

```
companions PRESENT   twig --help (20 runs):  130 113 114 104 100 129 114 108 109 113
                                             126 102 102 123 118 114 116 107 102 112   (ms)
companion MISSING    twig --help  run 1:  6499 ms   ("Installing companion tools...")
                                  run 2:   146 ms
                                  run 3:   128 ms
```

Positive control — deleting `twig-mcp.exe`/`twig-tui.exe` in an isolated copy reproduced the spike
at **6499 ms**; negative control — restoring the `.twig-version` marker with companions still
absent returned to **119 ms**, exercising the Phase-2 early return at
`CompanionFirstRunCheck.cs:46-52`. The spike is a real, reproducible, network-bound stall on a
command that should touch nothing.

This also explains the **intermittency** the Question flagged as "consistent with a cache/expiry
boundary": it is not an expiry clock. It is a one-shot-per-version event — the marker is written
at `CompanionFirstRunCheck.cs:90-93` *after* the attempt, so the spike fires on the first
invocation following an upgrade (or any run where the marker is missing), then never again until
the version changes. The 5085/5062 pair at the head of one batch and runs 1-2 of the next are
consistent with the marker being rewritten between batches, not with a 5-second timeout.

**The `~5080ms ≈ 5000ms timeout` coincidence in the Question is a false match.** It anchored the
investigation on two timeouts that are both unreachable from `--help`.

**Ruling: make the composition root lazier? No — it already is.** The fix is narrower and is not
a DI-shape change: **`--version`, `-h/--help`, `help`, and the unknown-command path must return
before any startup side effect.** Move `SelfUpdater.CleanupOldBinary()` (`Program.cs:27`) and
`CompanionStartup.RunFirstRunCheck()` (`:31`) below the fast-exit block at `:122-156`. Neither
belongs above it: cleanup is filesystem churn, and the companion check is a *network install*
gated on nothing. This is a **correctness defect, not a latency-policy question**, and it is
independent of 0004 — as the Question suspected, but for a different reason than it proposed.

**This does not share a fix with 0007.** The Question's conditional ("if the 5s spike is eager
service construction, 0011 and 0007 share a fix") is false: the spike is pre-DI code in
`Program.cs`, above the composition root entirely. 0007 should not inherit this as a dependency.

### 2. Latency budget

Measured this session against the installed 0.84.3 binary, warm:

| path | measured | what it pays |
|---|---|---|
| `--version`, unknown command | 78-99 ms | process + runtime start only, no DI |
| `--help` (companions present) | 100-130 ms | + `SQLitePCL.Batteries.Init()`, UTF-8 console, cleanup probe |
| `list` | 138-187 ms | + full DI, config load, SQLite open |
| `show` | 254-325 ms | + read path and render |

**Budget: a local-only command (no remote fetch) must complete in under 400 ms; a
no-workspace-touching command (`--version`, `--help`, unknown command) must complete in under
150 ms and must make ZERO network calls.** The second half is the regression-guard that matters —
it is a *behavioural* assertion, not a timing one, and so it is cheap and non-flaky to test:
assert that the fast-exit paths construct no `HttpClient`. A pure wall-clock threshold in CI would
be flaky and is not proposed.

### 3. Profiling mode — NO, not now

`--profile` / `TWIG_PROFILE=1` emitting a per-phase span breakdown is **rejected as premature**.
The Question justifies it as "the instrument the other tickets need: 0004 and 0005 are both
arguing about work that nobody has timed" — but 0004 and 0005 have both since **closed**, on
structural grounds (ownership of the reconciliation decision; the ADO-can-rebuild-it durability
test), not on timing. The instrument would arrive after the arguments it was meant to settle.
The measurements above took a shell loop and no product surface. Build the instrument when a
question needs it, and let it be shaped by that question.

### 4. Instrumentation seam — DELETE, do not extend

**`TwigActivitySource` fails the deletion test.** The machinery exists and is instrumented at 20
call sites — `CommandActivityScope` wrapping ~18 commands (`src/Twig/Commands/CommandActivityScope.cs:33`),
render operations (`src/Twig/Rendering/SpectreRenderer.cs:679,1495`), and ADO calls
(`src/Twig.Infrastructure/Ado/AdoRestClient.cs:293,351,521`) — but **there is no
`ActivityListener` registered anywhere in `src/`**. A tree-wide grep for `ActivityListener`,
`OpenTelemetry`, and `AddOpenTelemetry` across `src/**/*.cs` and every `.csproj` returns exactly
one hit: a comment inside `TwigActivitySource.cs:9` explaining what happens when no listener is
registered. The only listeners that exist are in the test project
(`tests/Twig.Domain.Tests/Diagnostics/TwigActivitySourceTests.cs:21,82`).

So by its own documented contract — "When no `ActivityListener` is registered,
`ActivitySource.StartActivity` returns null and instrumentation is effectively zero-cost"
(`TwigActivitySource.cs:9-11`) — **every span in twig is null at runtime, and every tag written
onto it is discarded.** The `TraceTags` allowlist governs data that reaches no consumer. The tests
pass because they install a listener the product never installs; they verify the machinery
against itself. This is the ticket's own predicted finding — *"the machinery exists and nothing
consumes it"* — confirmed.

**Ruling: delete `src/Twig.Domain/Diagnostics/` and its 20 call sites rather than adopt
OpenTelemetry.** Adopting OTel — `ActivitySource` + `ILogger` in Domain, exporter chosen at the
composition root — is rejected on the constraint 0001 already set: **twig is a single-user local
tool**. Distributed tracing's value is correlating spans across processes and hosts; twig is a
per-invocation CLI whose entire "trace" is one process that lives ~150 ms and then exits. It
would also add a dependency to `Twig.Domain`, which today has none, to serve no live consumer.
The three-composition-roots concern the Question raises (via 0007) is real but is an argument
about *where a thing is registered*, not an argument that the thing should exist.

If a consumer ever appears, the seam to re-add is one `ActivityListener` at the composition root
— a few lines. Keeping 200 lines of unreachable machinery on the chance that day comes is the
more expensive bet, and it has already cost: it made this ticket's telemetry hypothesis look
plausible for the length of an investigation.

`ITelemetryClient` is a separate call and is **kept**: unlike the ActivitySource it has a live
implementation and a real consumer path (`TelemetryHelper` and four commands), it is opt-in and
dark by default (`TelemetryClient.cs:48-55`), and it is behind a Domain interface whose
App-Insights envelope is confined to the Infrastructure impl. It is unused-but-reachable, not
unreachable. Do not extend it either.

### 5. Local traces to Grafana — OUT OF SCOPE for this map

The owner-stated goal of local logs and traces landing in Grafana is real, but it is a **separate
product decision, not a step on the route to this map's destination**, and it cannot proceed on
this map's terms: it presupposes §4 going the other way. Its cost is not the exporter — it is
standing up and keeping an OTLP collector plus Grafana (Alloy/Tempo/Loki, or the all-in-one
OTEL-LGTM container) alongside a CLI whose commands last 150 ms, to answer questions nobody has
yet posed. On the privacy boundary the Question asks about: no boundary work is owed today,
because the only shipping egress is `ITelemetryClient`, which is inert unless the user sets two
environment variables themselves — an explicit opt-in, not a default.

If this is revived, it should be as its own effort with the question stated as "what long-term
question about twig usage do we want to answer, and what is the cheapest thing that answers it"
— quite possibly a JSONL file the user can grep, given a single-user local tool. Note that any
diagnostic *output* proposal is constrained by 0002: each surface owns its own presentation, so
this must not become a cross-surface logger. Recorded on the map under **Out of scope**.

### Follow-on work this creates

1. **A defect ticket for the startup ordering** — move `Program.cs:27` and `:31` below the
   fast-exit block at `:122-156`, with a regression test asserting the fast-exit paths make no
   network call. This is the only code change 0011 produces, and it is a real user-visible bug:
   a 6.5-second `twig --help` after every upgrade.
2. **A deletion ticket for `Twig.Domain/Diagnostics/`** — 3 files, 20 call sites, and the test
   file that verifies the machinery against a listener only it installs.

Both are `type: task`; neither is blocked by anything still open.
