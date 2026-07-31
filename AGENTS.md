# Repository guidance

## Build & test

`global.json` pins SDK **11.0.100-preview.5.26302.115** with `rollForward: latestFeature`.

That SDK **is** installed system-wide here (`C:\Program Files\dotnet`), so plain `dotnet`
on the PATH works. No exports are needed for a normal build:

```bash
dotnet build src/Twig/Twig.csproj -m:1
```

### History: the old preview.3 pin (#333)

The repo used to pin **11.0.100-preview.3.26207.106** with `rollForward: disable`, because
`src/Twig.Domain/Common/CompilerPolyfill.cs` declared `UnionAttribute` / `IUnion` into
`System.Runtime.CompilerServices` — and from preview.5 the runtime ships those types itself,
producing `error CS0433: The type 'IUnion' exists in both 'Twig.Domain' and 'System.Runtime'`.

That is now resolved by scoping the shim to `net10.0` only (`Twig.Domain.csproj` removes it
from the compile for every other TFM), so the pin no longer needs to hold newer SDKs back.
The shim still exists and is still required for the `net10.0` target, whose ref pack does not
carry the types — do not delete it until `net10.0` is dropped.

**If you have a stale `DOTNET_ROOT` exported** (e.g. `$HOME/.dotnet-p3` from the old
instructions), test hosts fail with *"You must install or update .NET to run this
application"* listing only preview.3. `unset DOTNET_ROOT`.

### Canonical test command

**Use `tools/run-tests.sh`.** It runs the four suites serially with the right
filters and prints a single reconciled verdict per suite:

```bash
tools/run-tests.sh              # all four
tools/run-tests.sh Cli Domain   # a subset
```

It exits non-zero unless every suite is a genuine, unaborted pass. Grep its
output for `TWIG-VERDICT` — never for `Passed!` (see "Reading test results").

The underlying commands, if you need to run one by hand. `dotnet test` accepts
only **one** project per invocation, and two concurrent runs collide over shared
build output (producing a bogus `SQLitePCL DllNotFoundException`). Run them
**serially**:

```bash
dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo --filter "FullyQualifiedName!~BinaryLauncher"
dotnet test tests/Twig.Infrastructure.Tests/Twig.Infrastructure.Tests.csproj --nologo
dotnet test tests/Twig.Mcp.Tests/Twig.Mcp.Tests.csproj --nologo
dotnet test tests/Twig.Domain.Tests/Twig.Domain.Tests.csproj --nologo
```

`BinaryLauncherTests` is excluded because it spawns a child binary that cannot
resolve the SQLite native lib under a user-local SDK, killing the test host
mid-run. It is environmental, not a repo defect, and passes in CI.

Building the whole solution works as of #342. `tests/Twig.Benchmarks` used to fail
with a `CS0433 ILoggingBuilder` ambiguity — BenchmarkDotNet pulls
`Microsoft.Extensions.Logging` **2.1.1** transitively (via
`Microsoft.Diagnostics.NETCore.Client`), whose `netstandard2.0` assembly defines
`ILoggingBuilder` alongside the one in the shared framework. A direct pinned
`PackageReference` in that csproj now wins over the transitive version.

This was tolerable while it was local-only, but the preview.5 SDK move (#338) made
CI run a bare `dotnet build` over everything, turning it into a red check on every
PR. If it returns, check for a transitive `Microsoft.Extensions.Logging` below 10.x:

```bash
dotnet list tests/Twig.Benchmarks/Twig.Benchmarks.csproj package --include-transitive | grep Logging
```

### Reading test results

**Trust the process exit code, not the summary line.** An aborted run still prints
a clean-looking `Passed! - Failed: 0` with a smaller total, and a TRX report's
counters only describe the portion completed before the host died.

`tools/run-tests.sh` exists precisely so this is not a judgement call. It
reconciles the exit code, the abort markers, and the test total, and emits one
verdict line that cannot grep as a pass unless the run really passed:

```bash
tools/run-tests.sh Cli | grep TWIG-VERDICT
# TWIG-VERDICT Cli: PASSED (2941 tests) [log: artifacts/test-logs/Cli.log]
# TWIG-VERDICT OVERALL: PASSED
```

If you must invoke `dotnet test` directly, capture the exit code and include
`Aborted` in the grep — `grep -E "Passed!|Failed!"` alone matches the false-green
summary line an aborted run prints:

```bash
dotnet test ... > log 2>&1; echo "EXIT=$?"
grep -E "Passed!|Failed!|Aborted|\[FAIL\]" log
```

Reporting "suite green" from a summary grep while the process exits non-zero has
already cost one bogus issue report (#257, closed as invalid), and the underlying
hang that produced those aborted runs was #311.

### Diagnosing an aborted run (#311)

When a run aborts, vstest does **not** name the test that was in flight, and a TRX
does not rescue you — it is written at the end of a run and only describes tests
that COMPLETED, so the in-flight one is absent rather than marked.

`tools/find-hung-test.sh` closes that gap. An assembly-level xUnit hook
(`tests/Twig.Cli.Tests/TestSupport/TestProgressTrace.cs`) appends a flushed
START/END line per test to the file named by `TWIG_TEST_TRACE`; the script
reconciles them and reports the last START with no END.

```bash
tools/find-hung-test.sh        # one traced run
tools/find-hung-test.sh 20     # loop until an abort is captured
```

The trace is **opt-in** — with `TWIG_TEST_TRACE` unset the hook returns immediately,
so normal runs pay nothing.

🔴 **Do not map an abort's reported test count onto an execution order.** The count
in `Passed: N` looks like an index into the run, but xUnit's order is **not stable
between runs**: eight traced runs of the Cli suite diverged from each other at
index 0 while executing the same 3018 tests. Naming a suspect that way produces a
confident wrong answer.

**What the traces establish about the Cli suite's time budget:** all 3018 tests
execute in **~7 s** of summed test-body time (~10 s wall between first and last
boundary), against a 300 s session timeout. A healthy full run is ~15 s. So an
abort is *not* the suite gradually running out of budget — ~97% of the timeout is
spent outside test bodies, and something must block for **minutes** to reach 300 s.

The known consumer of that untraced time is `BuildFixture` (`AotSmokeTests.cs`),
which shells out to a nested `dotnet build` (up to a 5-minute internal timeout) in
its **constructor** — untraced, because it is fixture setup rather than a test body.
`AotSmokeTests` is excluded by the runsettings `Category!=Interactive` filter, but
`OutputFormatEntrypointTests` shares the same fixture and is deliberately *not*
excluded, so the nested build runs on every normal Cli run. It was measured at
~2.1 s idle; it is unbounded under load, and a nested build contending with the
outer one is exactly the kind of machine-level contention that both observed
failure clusters are consistent with.

That file's own source already records this failure mode: a comment on
`AcceptedFormat_IsNotRejectedByTheEntrypointGuard` notes that running an accepted
format through the binary "pays for the 0018 startup side effects — a blocking
GitHub companion download — and that blew the 300 s test-run budget and aborted the
host." The negative cases avoid it only because `Program.cs` validates `-o` above
the startup side effects.

This is a **strong, evidence-backed suspect, not a confirmed root cause** — 8
traced runs did not reproduce the abort, so no trace names it yet. Run
`find-hung-test.sh` in a loop on a loaded machine to capture one.

### 🔴 REPRO CAPTURED — the stall is NOT inside a test body

Reproduced on attempt 11/25 under load (16 CPU spinners plus two competing
`dotnet build` loops against `src/Twig`). The captured trace settles it:

```
Aborting test run: test run timeout of 300000 milliseconds exceeded.
Passed!  - Failed: 0, Passed: 1218, Skipped: 0, Total: 1218, Duration: 8 s
Test Run Aborted.
```

**Every one of the 1218 STARTs had a matching END.** No test was in flight. The
reconciler's own verdict was `IN-FLIGHT AT ABORT: none`.

The timeline is unambiguous:

| | |
|---|---|
| first test START → last test END | **8.8 s** (1218 tests, 40% of the suite) |
| last trace write | 03:56:09 |
| final log write (host killed) | 04:00:58 |
| **host alive but running NO tests** | **~289 s** |

So the suite does not slow down and run out of budget. It executes normally, then
**stops dispatching tests entirely** and sits idle until the 300 s session timeout
kills it. `Duration: 8 s` on the false-green summary is truthful — it is the real
in-test time; the other ~290 s is the hang.

**This falsifies the natural reading of the suspect list.** The stall is not a slow
or hung test, so per-test theories (SQLite pools, static state, a specific fixture's
work) cannot explain it on their own. `BuildFixture`'s nested `dotnet build` is a
real cost — reliably the largest untraced gap, 2.1 s idle scaling to ~6.2 s under
load, 8/8 runs — but it is **not** the abort: the abort happened at ~40% of the
suite, and the entrypoint tests run near the end and were never reached.

The evidence points at the **runner/host boundary** rather than at test code: the
vstest host stops requesting work while the process stays alive. That is consistent
with a communication or scheduling stall between `dotnet test` and the test host
under CPU/IO contention, which also explains why it reproduces on a busy GitHub
runner and vanishes on a re-run of the identical commit.

Reproducer, for whoever picks this up:

```bash
tools/repro-311/cpu-load.sh 3600 16 &   # CPU contention
tools/repro-311/build-load.sh 1800 &    # competing MSBuild
tools/repro-311/build-load.sh 1800 &
tools/find-hung-test.sh 25
```

Hit rate was 1 in 11 under that load. CPU spinners **alone** did not reproduce it
in 10 attempts; it aborted on the next attempt after the competing builds were
added, so the MSBuild contention appears to be the load that matters. Do not "fix"
this by raising the timeout — it would convert a ~290 s dead hang into a longer one.

### The `--diag` probe (#41) — instrumented, not yet triggered

`tools/diag-hunt.sh` runs the suite with the boundary trace **and** vstest `--diag`
enabled together, so one captured abort carries all three layers.
`tools/diag-analyze.py` lines them up and reports which side fell silent first.

🔴 **Comparing the LAST line of the runner and host logs proves nothing.** The
session timeout tears both sides down within ~20 ms of each other, so the final
lines always look synchronised. The informative moment is where the **silence
starts** — the longest gap between consecutive messages on each side. The analyzer
reports that instead.

**Result so far: 70 attempts under load, no timeout repro captured with `--diag`
on.** That is recorded as a negative result, not a fix. Two stressor defects were
found and corrected along the way, each of which had silently invalidated a full
30-attempt cycle:

1. **The build-load loop was a no-op.** `dotnet build` on an up-to-date project
   takes ~3.4 s, spawns no compiler processes, and applies almost no contention.
   The tell was run duration: ~29 s per attempt versus the ~40 s seen in the hunt
   that *did* reproduce. **Verify your load is real** — `pgrep -cf csc.dll` should
   be non-zero while a hunt runs.
2. **Forcing a rebuild of `src/Twig` broke the suite.** The Cli suite's own
   `BuildFixture` builds that same project, so the loop collided with it and five
   `OutputFormatEntrypointTests` failed with *"Build failed or timed out"*. That is
   self-inflicted, **not** #311 — it aborts on a real `[FAIL]` rather than on the
   timeout. `build-load.sh` now builds `Twig.Domain` into a private output
   directory, and `diag-hunt.sh` exits 2 on real failures rather than banking them
   as a capture.

An open question the negative result raises: whether `--diag` itself perturbs the
timing enough to suppress the hang (it writes ~5 MB per run per side). Worth
testing by alternating traced-only and diag-enabled hunts before concluding the
bug moved. (Probed — see "The A/B under load" below. Short version: `--diag` is now
**0 captures in 90 loaded attempts** and should not be used for further hunting.)

### 🔴 CORRECTION — heavy load is NOT required to trigger the hang

`tools/repro-311/perturbation-ab.sh` alternates diag-ON and diag-OFF attempts in
one run (interleaved, not two campaigns, so machine drift hits both arms evenly).
On its **first validation attempt** it captured the hang — diag OFF, on an
**idle machine**: load average 2.4, **zero load generators running**.

```
Aborting test run: test run timeout of 300000 milliseconds exceeded.
Passed!  - Failed: 0, Passed: 1977, Skipped: 0, Total: 1977, Duration: 3 s
Test Run Aborted.
```

Trace: 1977 STARTs, 1977 ENDs, **nothing in flight**, 3.1 s of test-body time in a
303 s run — the same shape as both earlier captures.

**This corrects an assumption carried since the first repro.** The load generators
were built because the first two captures happened under load, and that
correlation was mistaken for *necessity*. It is not strictly necessary — an idle
box did reach the trigger once.

⚠️ **But do not over-read this heading.** A follow-up 120-attempt idle A/B and two
further idle probes produced **0 hits in 166 more attempts**, putting the idle
rate at ~1 in 129 against ~1 in 11 under load. The accurate statement is *load is
not strictly required, but it raises the hit rate by about an order of
magnitude* — not *"you can hunt this on an idle box"*. See the 120-attempt
section below before planning a hunt.

Two hypotheses tested and **killed** immediately afterwards, recorded so nobody
re-derives them:

- **"It needs load."** Disproven by the idle-box capture above.
- **"It's the first run after a rebuild."** The one idle hit followed a fresh
  build, while 30 consecutive warm-state runs were clean — a tempting lead.
  Tested directly with 8 × (rebuild → run): **0/8 hits**. No correlation.
  (Distinct from the disproven "cold worktree / first run after checkout" on #39;
  this was rebuild-of-the-assembly, and it is dead too.)

**Current hit-rate data**, all on an idle box unless noted:

| Conditions | Attempts | Hits |
|---|---|---|
| under heavy load, trace only | 11 | 1 |
| under heavy load, `--diag` on | 70 | 0 |
| idle, alternating A/B | 30 (15 per arm) | 0 |
| idle, rebuild-then-run | 8 | 0 |
| idle, single validation run | 1 | **1** |

The observer-effect question is therefore **still open**: the A/B produced 0 hits
in *both* arms, which says nothing about `--diag` and only says that 15 attempts
per arm is too few at this base rate. Do not read it as clearing `--diag`.

### The 120-attempt A/B: 0/60 vs 0/60 — and what that actually means

Re-ran the A/B at **60 attempts per arm** on an idle box (~11 s per run, ~25 min
total). Result: **0 hits in both arms.**

| Arm | Runs | Hits |
|---|---|---|
| `--diag` OFF | 60 | 0 |
| `--diag` ON | 60 | 0 |

Since the OFF arm also produced nothing, this **still cannot answer the
observer-effect question** — you cannot measure suppression of an event that
isn't occurring in the control. What it does establish is sharper and more
useful: **the idle-box hit was not the start of a reproducible idle regime.**

That single hit now stands at **1 in 129 idle attempts**, versus 1 in 11 under
heavy load. Reading it as "an idle box reproduces this" was over-reading one
event — it is better described as *the trigger is not strictly load-gated, but
load raises the rate by roughly an order of magnitude*.

A further hypothesis, tested and **killed**: the one idle hit was the first-ever
`dotnet test` in a brand-new worktree, and all 120 clean runs shared one warm
worktree — a tempting explanation. Probed directly with 8 × (fresh worktree →
build → one run): **0/8**. Consistent with #39, which already recorded
first-run-after-checkout as disproven. (Note the distinction #39 rules out:
first-run is not *sufficient*. These 8 attempts also give no support for it being
a strong *risk factor*.)

**Full hit-rate table:**

| Conditions | Attempts | Hits | Rate |
|---|---|---|---|
| heavy load, trace only | 11 | 1 | ~9% |
| heavy load, `--diag` on | 70 | 0 | 0% |
| heavy load, A/B diag OFF arm | 20 | 1 | 5% |
| heavy load, A/B diag ON arm | 20 | 0 | 0% |
| heavy load, `dispatch-watch.sh` (#42) | 25 | 0 | 0% |
| idle, A/B (both arms) | 150 | 0 | 0% |
| idle, rebuild-then-run | 8 | 0 | 0% |
| idle, cold worktree first-run | 8 | 0 | 0% |
| idle, single validation run | 1 | 1 | — |

🔴 **Practical consequence for the next investigator: hunt under heavy load.** It
is the only condition with a demonstrated non-trivial hit rate (1 in 11). Idle
hunting has now cost 167 attempts for a single capture. Use
`tools/repro-311/cpu-load.sh` plus two `build-load.sh` instances, verify the load
is real with `pgrep -cf csc.dll`, and expect roughly one hit per dozen attempts.

### The A/B under load: 1/20 OFF vs 0/20 ON — the control arm finally produced a hit

The A/B was re-run **under heavy load** (16 CPU spinners plus two `build-load.sh`
instances, load average 19-40 on 20 cores, `pgrep -cf csc.dll` non-zero
throughout), which is the condition the previous section called for.

| Arm | Runs | Hits |
|---|---|---|
| `--diag` OFF | 20 | **1** |
| `--diag` ON | 20 | 0 |

The hit landed on **attempt 1**, diag OFF, at 310 s. Its shape is identical to all
three previous captures:

```
Aborting test run: test run timeout of 300000 milliseconds exceeded.
Passed!  - Failed: 0, Passed: 1092, Skipped: 0, Total: 1092, Duration: 7 s
Test Run Aborted.
```

1092 STARTs, 1092 ENDs, **nothing in flight**, 7.3 s of test-body time spanning
00:34:53→00:35:00 inside a 310 s run. Four captures now agree: the runner stops
dispatching and idles out.

**What this does and does not settle.** Unlike the idle A/B, the control arm
produced a hit, so the arms are no longer both empty — the result is *consistent
with* `--diag` suppressing the hang. It is **not proof**. One hit in twenty is far
too few to distinguish suppression from luck: at the observed base rate, a 0/20 ON
arm is an unremarkable outcome even if `--diag` changes nothing. The script says as
much in its own verdict rather than letting the reader over-read it.

🔴 **The actionable consequence is the same either way: stop hunting with `--diag`.**
Across 90 loaded diag-ON attempts (70 in the original hunt, 20 here) it has produced
**zero** captures, while trace-only has produced two in 31. Whether that is
suppression or expensive bad luck, `--diag` is not the instrument that will catch
this. A lighter probe is needed — one that observes the runner/host dispatch
boundary without writing ~5 MB per side per run.

### The replacement probe: `dispatch-watch.sh` (#42)

`tools/repro-311/dispatch-watch.sh` is that lighter probe. It answers the one
question left open — **at the moment dispatch stops, is the runner waiting on the
host, or the host waiting on the runner?**

It watches the existing `TWIG_TEST_TRACE` boundary file's mtime from **outside**
both processes. A gap of `TWIG_311_STALL_SECS` (default 45 s) trips it, and on trip
it snapshots both sides:

- the `vstest.console` ↔ `testhost` **TCP socket pair** (`ss -tnpi`) — `Send-Q` /
  `Recv-Q` are the decisive evidence for which side stalled;
- managed stacks of every thread (`dotnet-stack report`);
- per-thread kernel state and wait channel from `/proc`, which still works if the
  diagnostics IPC endpoint is itself wedged.

`tools/repro-311/dispatch-analyze.sh <capture-dir>` applies the decision rule.

**Cost during a healthy run: one `stat` per second on one file, from a separate
process.** Nothing is written by either side under test and no diagnostic channel
is opened until *after* a stall has already happened — so unlike `--diag` it cannot
perturb the timing that produces the bug.

**Prove the instrument before betting a hunt on it.** Both halves self-test:

```bash
tools/repro-311/dispatch-watch.sh --selftest-detector   # gap detector, no test run
TWIG_311_SELFTEST=1 tools/repro-311/dispatch-watch.sh   # snapshot path, real live PIDs
```

The healthy-run self-test **force-trips** rather than lowering the threshold, and
that is deliberate: a healthy run contains no gap worth waiting for. Measured here,
3018 tests execute in **7.3 s wall with a largest in-trace gap of 1.53 s**, and
`BuildFixture`'s nested build — the largest untraced cost — happens *before the
first trace line*, in a window the watcher cannot see by construction.

🔴 **`pgrep -cf csc.dll` is a STALE load-validity check on the preview.5 SDK.** Roslyn
compiles inside the persistent `VBCSCompiler` server, so `csc.dll` reads **zero** even
under genuine heavy build load — following the old rule literally would make you
conclude a working stressor was a no-op. Check these instead:

```bash
ps -eo args --no-headers | grep -c '[V]BCSCompiler'   # Roslyn server up
ps -eo args --no-headers | grep -c '[b]uild-load.sh'  # loops actually alive
cut -d' ' -f1-3 /proc/loadavg                         # >20 on 20 cores
```

Per-attempt duration remains the best single tell: **~11 s idle vs 42-75 s under real
load** for the Cli suite.

**First loaded hunt with it: 25 attempts, 0 captures.** Conditions verified during the
run, not assumed: 16 spinners alive, two `build-load.sh` loops cycling a real
`--no-incremental` rebuild every ~2 s, `VBCSCompiler` up, load average 21→35 on 20
cores, per-attempt duration 42-75 s against ~11 s idle. No trip, no abort, no `[FAIL]`.

🔴 **Read this as a null result about the BUG, not about the probe.** The two things
it does not say:

- It does not clear or condemn `dispatch-watch.sh`. The instrument was proven
  separately by its two self-tests — the detector trips on a stalled trace, and the
  snapshot path produced a matched socket pair plus host stacks naming
  `MessageLoopAsync` / `DefaultEngineInvoker` against real live PIDs. A hunt with no
  abort in it never exercises the probe, so it cannot measure it.
- It does not re-open "does load matter". At the loaded rate of ~1 in 11-20, a 0/25
  run is an ordinary outcome. **Expect to need 40-60 loaded attempts**, and note that
  the two trace-only captures both landed in the first few attempts of their hunts —
  arrival is clumpy, so do not read an early clean streak as evidence of anything.

**The remaining gap, stated honestly:** the runner-vs-host question is still
unanswered. The probe that can answer it now exists and is proven to fire; it has
simply not yet been pointed at a live abort.

🔴 **`pgrep`/`ps` counts can match your own wrapper.** A bare `pgrep -cf cpu-load.sh`
returned `1` here when the count was truly `0` — it matched the shell command running
the check. Use a bracketed pattern (`grep -c '[c]pu-load.sh'`) so the checker cannot
count itself. This produced a false "load is running" reading during #42.

## Testing conventions

Regression tests must **fail on the unfixed code**. A test that passes both before
and after proves nothing. To check, add the fix's tests to a detached worktree at
the pre-fix SHA and confirm they fail there:

```bash
MSYS_NO_PATHCONV=1 git worktree add --detach ../twig-baseline <pre-fix-sha>
```

Watch for fixtures that silently degrade into the happy path — e.g.
`ConflictResolver.Resolve` short-circuits to `NoConflict` when local and remote
revisions match, so a conflict-path test must advance the remote revision
(`remote.MarkSynced(n)`) or the branch under test never runs. Where a fixture has
such a precondition, assert it explicitly so a future setup regression can't
hollow the suite out.

`MergeResult` is a `union`: pattern-match the case (`result is HasConflicts`).
`ShouldBeOfType<HasConflicts>()` fails against the wrapper type.

## Git

The repo **squash-merges**, so branch SHAs are not ancestors of `main` after merge —
`git merge-base --is-ancestor` returning false is expected, not a failed merge.
Verify content landed instead:

```bash
git show origin/main:path/to/File.cs | grep -c "YourNewSymbol"
```

GitHub auto-close keywords do not chain: `Fixes #253 and #252` closes only #253.
Repeat the keyword — `Fixes #253, fixes #252`.
