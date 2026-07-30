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
bug moved.

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
