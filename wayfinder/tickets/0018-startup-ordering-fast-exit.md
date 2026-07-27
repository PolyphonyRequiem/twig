---
id: 0018
title: Startup ordering — no side effects above the fast-exit
type: task
status: closed
blocked_by: []
---

## Question

Graduated from [Startup cost and observability](0011-startup-and-observability.md), which found
this by measurement. Nothing to decide — the decision is made; this is the code change.

`twig --help` takes **6.5 seconds** on the first invocation after any upgrade. Two startup side
effects run unconditionally *above* the fast-exit block in `src/Twig/Program.cs`:

- `SelfUpdater.CleanupOldBinary()` — `src/Twig/Program.cs:27` (filesystem churn)
- `CompanionStartup.RunFirstRunCheck()` — `src/Twig/Program.cs:31` (**blocking GitHub release
  download, 60-second budget**, via `.GetAwaiter().GetResult()` at `:1155-1156`)

The fast-exit block sits below them at `src/Twig/Program.cs:122-156` and handles `--version`
(`:123`), `-h` / `--help` / `help` (`:128`), the no-args smart landing (`:133`), and the
unknown-command interception (`:151`). Every one of those paths pays for a network install it can
never need.

Measured on the installed 0.84.3 binary (0011, reproduced under control):

```
companions PRESENT   --help x20:  100-130 ms
companion MISSING    --help run1:    6499 ms   ("Installing companion tools...")
                            run2:     146 ms
```

Negative control: restoring the `.twig-version` marker with companions still absent returns to
119 ms, exercising the Phase-2 early return at
`src/Twig.Infrastructure/GitHub/CompanionFirstRunCheck.cs:46-52`.

It is once-per-version rather than every run — the marker is written at
`CompanionFirstRunCheck.cs:90-93` *after* the attempt — which is exactly why it survived: it looks
intermittent, and 0011 initially misread it as a cache-expiry boundary.

### What to do

1. Move `Program.cs:27` and `Program.cs:31` to **below** the fast-exit block at `:122-156`.
   Both must still run for real commands — this is a reordering, not a removal.
2. Note that `SQLitePCL.Batteries.Init()` (`:14`) and the UTF-8 console setup (`:17-24`) also sit
   above the block. Decide whether they move too: they are local and cheap (the `--version` path
   measures 78-99 ms against `--help`'s 100-130 ms), so the default is **leave them** unless
   moving them is free. Do not let this expand into a general startup refactor.

### Acceptance

The regression guard is **behavioural, not a wall-clock threshold** — a timing assertion in CI
would be flaky and is explicitly not wanted (0011 §2). Assert that the fast-exit paths
(`--version`, `--help`, unknown command) perform **zero network calls** — e.g. that no
`HttpClient` is constructed on those paths.

Per `AGENTS.md`, the test must **fail on the unfixed code**: confirm it fails at the pre-fix SHA in
a detached worktree before claiming it guards anything.

Budget from 0011 §2, for reference: **<150 ms and zero network** for a no-workspace command;
**<400 ms** for a local-only command.

## Answer

**Done — reordering only, no behaviour removed.**

`SelfUpdater.CleanupOldBinary()` and `CompanionStartup.RunFirstRunCheck()` moved from
`src/Twig/Program.cs:27` / `:31` to immediately **below** the fast-exit block, just above
`app.Run(args)`. Both still run for every real command; `--version`, `-h`/`--help`/`help`, the
no-args no-workspace landing, and the unknown-command interception now return before either
executes. The no-args *smart landing* case (`args = ["show"]`) falls through and still gets both,
which is correct — it is a real command.

Per the ticket's item 2, `SQLitePCL.Batteries.Init()` and the UTF-8 console setup were **left in
place**. They are local and cheap and moving them was not free (both are needed by paths that read
the DB or print Unicode badges before routing).

### Regression guard

`tests/Twig.Cli.Tests/StartupOrderingTests.cs` — 4 tests, source-ordering assertions on
`Program.cs`, deliberately **not** a wall-clock threshold (0011 §2: timing assertions in CI are
flaky, explicitly not wanted). It asserts each side-effecting call is present (still runs for real
commands) *and* positioned after the last fast-exit return, plus that no `HttpClient` is
constructed above the block. A precondition test asserts the four fast-exit paths still exist, so
a future refactor cannot hollow the guard into a no-op.

Verified against a detached worktree at pre-fix `e899de46`: **2 of 4 FAIL** (both
`StartupSideEffect_RunsBelowTheFastExitBlock` cases). Post-fix: 4/4 pass.

### Facts later tickets depend on

- **The load-bearing evidence is the marker file, not the stopwatch.** Running `--help` from a
  directory with companions absent and no `.twig-version`: the pre-fix binary **wrote
  `.twig-version`** (proving `RunFirstRunCheck` reached its Phase-2 install attempt on the help
  path); the post-fix binary **did not create it at all**. That is the direct observable that the
  side effect no longer runs on a fast-exit path.
- **Wall-clock numbers here are NOT a clean before/after and should not be quoted as one.**
  Measured on a local `-c Release` (non-AOT) build, both revisions: pre-fix cold 2208 ms / warm
  ~266 ms; post-fix cold 3027 ms / warm ~394 ms. The post-fix figure being *higher* is measurement
  noise plus JIT-vs-AOT overhead, and this box could not reach the GitHub release endpoint, so the
  6.5 s download that motivated the ticket never occurred in *either* run. The 6499 ms figure in
  0011 was measured on the published 0.84.3 AOT binary with the network reachable — reproducing it
  requires that setup. Anyone re-measuring should publish AOT and assert on the marker file.
- The fast-exit block is now a real ordering boundary with a guard behind it. Anything added above
  it must stay side-effect-free and network-free, or `StartupOrderingTests` fails.

PROPOSED follow-on (no ID assigned, per instructions): *Publish-AOT startup timing harness* — a
scripted, network-reachable reproduction of the 0011 measurement so startup budgets
(<150 ms no-workspace, <400 ms local-only) can be checked deliberately out-of-band rather than
guessed at from local Release builds.

