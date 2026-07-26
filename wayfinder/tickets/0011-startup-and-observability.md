---
id: 0011
title: Startup cost and observability
type: grilling
status: open
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

<!-- empty until resolved -->
