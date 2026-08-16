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

🔴 **A green `TWIG-VERDICT OVERALL` is necessary but NOT sufficient before you push.** This
script runs four suites; CI runs six and compiles the whole solution. `--pre-push` adds
CI's own commands and folds them into the verdict:

```bash
tools/run-tests.sh --pre-push
```

Why, what it catches, and the incident that forced it: see "Before you push: the script's
verdict is not CI's verdict" below.

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
mid-run. It is environmental, not a repo defect, and passes in CI — note that
these by-hand commands inherit that exclusion too, so they are no closer to CI's
verdict than the script is.

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

(Counts in this file are snapshots from whenever the surrounding note was written —
2941 here, 3018 in the #311 sections, 3191 in the next section — and the suite grows.
Treat the *shape* of each line as the guidance, never the number.)

🔴 **A usage error is a verdict too, and that is what makes the grep safe (AB#352).**
The script used to hard-exit on an unrecognised option or a mistyped suite name
*without printing any verdict at all*, so the mandated grep came back **empty** — and
empty output contains no `FAILED`, so a caller asking "did anything fail?" the
documented way saw nothing wrong. The rule pointed at the one hole in the instrument
built to close it. Every early exit now emits, on **stdout**:

```bash
tools/run-tests.sh Domian | grep TWIG-VERDICT
# TWIG-VERDICT OVERALL: FAILED (unknown suite 'Domian' (known: Cli Infrastructure Mcp Domain) — nothing ran)
```

`nothing ran` is deliberate: it distinguishes a usage error from a broken test, so
nobody goes hunting for a failure that does not exist. The diagnostic still goes to
stderr as well; the verdict is on stdout because that is all the documented grep sees.

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

### Before you push: the script's verdict is not CI's verdict

🔴 **A green `TWIG-VERDICT OVERALL` is necessary but not sufficient.** `tools/run-tests.sh`
is the right instrument for "did the tests I care about pass" — it reconciles an abort into
an honest verdict, which a raw `dotnet test` will not do for you. It is **not** a prediction
of CI, because it runs a deliberately narrower set:

| | `tools/run-tests.sh` | CI (`.github/workflows/ci.yml`) |
|---|---|---|
| Assemblies | **four** — Cli, Infrastructure, Mcp, Domain | **six** — those four plus `Twig.RenderTree.Tests` and `Twig.Tui.Tests` |
| Cli filter | `FullyQualifiedName!~BinaryLauncher` | none — `BinaryLauncherTests` runs |
| Compiles | only the four suites and what they reference | the **whole solution**, including `tests/Twig.Benchmarks` |
| External host probe | **only under `--pre-push`** (`TWIG-VERDICT DetailHostProbe`) | always, as its own step |

### The external host probe runs now (AB#341)

`samples/Twig.DetailHost` is the final gate of wayfinder-detail-projection ticket 0006 §10 —
consumer → public projection → host-owned renderer, from **outside** Twig. Its location is
load-bearing: `Twig.Domain` grants `InternalsVisibleTo` to every first-party test assembly, so
a test project would pass while proving nothing about an external consumer.

🔴 **It carried an acceptance floor returning exit 1 on any miss, and until AB#341 nothing
ran it.** The solution *builds* it, so a visibility regression was still caught — but the
floor itself was dead weight: the fixture could have stopped exercising all three field states
and no check would have noticed. A mechanism that exists, reads as protective, and is wired to
nothing is the same defect class as a green-looking aborted run, which is the thing this whole
script exists to abolish. **`--pre-push` now runs it and reconciles it into the verdict**, and
CI runs it as its own step.

**Two independent guards, because exit 0 alone is not enough.** A probe that dies before
printing anything exits non-zero; a probe gutted to `return 0` exits *clean and silent*.
Both are checked, and each fails with its own wording:

```
TWIG-VERDICT DetailHostProbe: FAILED (probe exit code 1 — the acceptance floor rejected the run)
TWIG-VERDICT DetailHostProbe: FAILED (probe exited 0 without printing PROBE OK — a silent probe is not a pass)
```

Both were proven by mutation: breaking the fixture produced the first, renaming the success
token produced the second, and the restored tree goes green — so neither guard is
always-FAILED. Note what the failing run looks like: **the other suites stay green**. It is the
AB#350 shape again, so read `OVERALL` or read every line.

🔴 **The runtime decision is explicit, not inherited: we ROLL FORWARD, we do not install a GA
runtime.** The sample targets `net10.0` GA *on purpose* — its csproj says so — to prove a
consumer is not dragged onto the preview SDK. That is the property under test, so **do not
retarget it** to make execution easier; doing so deletes the thing it proves. Installing
net10.0 GA in CI would let it run on its native runtime, but CI would then stop exercising the
case that actually ships: a real consumer on a machine with only a newer runtime. Roll-forward
*is* that consumer's experience, it keeps CI and the script identical, and it costs one
environment variable instead of a second SDK install. The accepted trade: we do not prove the
probe runs on net10.0 GA itself. `global.json` pins the SDK, so nothing drifts silently.

To run it by hand:

```bash
DOTNET_ROLL_FORWARD=Major dotnet samples/Twig.DetailHost/bin/Debug/net10.0/Twig.DetailHost.dll
```

A bare `dotnet run` fails with *"You must install or update .NET to run this application"* on a
box with only the preview SDK. That is environmental, not a defect.


`test.runsettings` is **not** a difference: `Directory.Build.props` sets
`RunSettingsFilePath`, so both paths pick it up. CI's `--settings` is CI being explicit.

That third row is a compile-time gap, not a test gap, and it has bitten before: the
`Twig.Benchmarks` `CS0433 ILoggingBuilder` break under "Build & test" above was invisible
locally for exactly this reason. `Twig.Benchmarks` is `IsTestProject=false` — CI **builds**
it and never tests it, and the script does neither.

Measured on this tree (`origin/main` @ `33e0f368`, clean, one run each):

```
tools/run-tests.sh          →  7913 tests  (Cli 3191, Infrastructure 1487, Mcp 1297, Domain 1938)
dotnet test --settings ...  →  8063 tests  (the same four with Cli 3193, + RenderTree 81, + Tui 67)
```

Note the Cli number **moves**, 3191 → 3193. `BinaryLauncherTests` is one `[Theory]` with two
`[InlineData]` rows, and those two tests live in a suite the script *does* run. That is the
cleanest available proof that the script's verdict and CI's verdict are different verdicts,
not the same one measured twice. (Re-measure rather than quoting these figures — they drift
with every card.)

**So run CI's own commands before pushing, in addition to the script.** `--pre-push` does
exactly that, and reconciles the result for you:

```bash
tools/run-tests.sh --pre-push
```

It runs the four suites as usual, then — **serially, never concurrently** — CI's own three
steps from `.github/workflows/ci.yml`:

```bash
dotnet restore \
  && dotnet build --no-restore \
  && dotnet test --no-build --settings test.runsettings
```

The wide run gets its own reconciled verdict line, and `OVERALL` covers both:

```
TWIG-VERDICT SolutionWide: PASSED (8082 tests across 6 assemblies) [log: artifacts/test-logs/SolutionWide.log]
TWIG-VERDICT OVERALL: PASSED
```

(As ever, the *shape* of that line is the guidance, never the number. The assembly count is
in the verdict deliberately, but read it as *evidence*, not as a guard: nothing asserts it
equals six, because hardcoding a total is how this file's counts go stale. The guards that
actually fail a narrowed run are the exit code and the invalid-argument marker.)

🔴 **The `&&` chaining is load-bearing, and `--pre-push` preserves it.** If the build fails
and `dotnet test --no-build` runs anyway, you get the trap two paragraphs down — a
green-looking run of whatever assemblies happen to still be on disk. Chaining means the test
step is never reached; the reconciler's invalid-argument marker is the second line of defence
for when a stale output directory survives a *successful* build.

The 300 s `TestSessionTimeout` in `test.runsettings` applies to the wide run too
(`Directory.Build.props` sets `RunSettingsFilePath`, so both paths pick it up), so an aborted
six-assembly run prints the same false-green `Passed!` described above — `--pre-push` runs it
through the same abort-marker check as every other suite, so you no longer reconcile it by
hand.

If you run the wide command by hand instead, you are back to judging it by its exit code, and
`grep -E "Passed!|Failed!|Aborted|\[FAIL\]"` **does not save you**: measured on a real broken
run it returned five matches, every one of them a green `Passed!` line, and nothing else.
It does not come back empty — it comes back *green*, which is worse. Prefer the flag.

The script's own guards are self-tested — negative and positive arms on both the log
reconciler and the usage-error exits, so neither an always-FAILED guard nor an
always-PASSED one could get through:

```bash
tools/run-tests.sh --selftest
```

(Add `-m:1` to the build if a parallel MSBuild is contending with another worktree. That is a
local convenience; CI builds without it.)

🔴 **The solution-wide build is not optional, and skipping it fails in a way that survives
every check except the exit code.** Verified here by moving one assembly's output aside and
re-running: the run reports `Passed!` for the other five, the **tail of the log is five clean
green lines**, and the only sign anything is wrong is a single line near the *top* —

```
The argument .../Twig.Tui.Tests.dll is invalid. Please use the /help option ...
```

— which contains neither `error` nor `fail`, so it survives the grep recipe above, and is
scrolled off by the time the run finishes. Only the non-zero exit code catches it. The one
command whose job is to be wider than the script silently becomes narrower than it, and looks
green while doing it.

**Cost, measured warm on this tree (both after a completed build, two runs each):**
`dotnet test --no-build --settings test.runsettings` took **74-76 s**; `tools/run-tests.sh`
took **92-99 s**. The wide command is the *cheaper* of the two despite running two more
assemblies, because it runs the six in **parallel** in one invocation while the script runs
four **serially** in four. Both are dominated by the Cli suite (~71 s of either). So
`--pre-push`, which runs both, roughly **doubles** the cost rather than multiplying it —
measured at ~2m50s warm on this tree.

🔴 **That parallelism is not a licence to run the two commands at the same time.** One
`dotnet test` parallelising across assemblies it owns is fine; two separate `dotnet test`
processes collide over shared build output and produce a bogus
`SQLitePCL DllNotFoundException` (see "Canonical test command"). Run the script and the wide
command one after the other — which is exactly what `--pre-push` does, and why it runs the
wide command *after* the four-suite loop rather than beside it.

### How the AB#350 gap bit: three suites green while one would not COMPILE

AB#241 below is a *test*-level gap. This one is cruder and arguably worse, because the
failing suite never ran at all.

A four-suite run during AB#350 reported:

```
TWIG-VERDICT Cli: FAILED (process exit code 1)
TWIG-VERDICT Infrastructure: PASSED (1496 tests)
TWIG-VERDICT Mcp: PASSED (1299 tests)
TWIG-VERDICT Domain: PASSED (1986 tests)
```

The Cli line is a `error CS1061` — the suite did not compile, so **zero** of its ~3200 tests
executed. The other three compiled and passed on their own, because the broken symbol lived
in `src/Twig` which only the Cli suite references.

🔴 **Three green verdict lines out of four is the shape a healthy run has too.** The failing
line is one row in a block of four, scrolled past on a wide terminal, and the three PASSED
counts are large and reassuring. `OVERALL: FAILED` is the only thing that distinguishes it —
so read `OVERALL`, or read every line, and never sample the middle of the block.

`--pre-push` folds the solution-wide build into the same verdict, which is what caught it:

```
TWIG-VERDICT SolutionWide: FAILED (solution-wide build failed (error CS) — the test step never ran)
```

That is the third table row above (**compiles: only the four suites** vs **the whole
solution**) firing in practice, and it is the reason the row is in the table rather than
being left as folklore.

### How the AB#241 gap bit

AB#241 added a `ProjectReference` from `Twig.Cli.Tests` to `Twig.Mcp` so one test could
drive both surfaces. That reads as ordinary. But `Twig.Mcp` is an **executable**, so the
reference copied `twig-mcp` into the Cli suite's output directory — and `BinaryLauncherTests`
clears `PATH` precisely to assert that binary is **not** discoverable. It found it, launched
the real MCP host in-process, and killed the Cli test host 48 tests in.

Locally: `TWIG-VERDICT OVERALL: PASSED`, because the script filters `BinaryLauncher` out.
On CI: red, on a change that touched no test the script runs. The fix — splitting the
guarantees by project so the Cli suite stops referencing an executable — is `c903ca95`.

The excluded test was not a gap in coverage. It is excluded for a real environmental reason
(under a user-local SDK it cannot resolve the SQLite native lib), and it passes in CI
whenever the Cli suite's output directory is clean — which is exactly the property it is
there to assert. The gap was in the **guidance**, which said where verdicts come from
without saying what that verdict does not speak for. Same class as #257: a green-looking
answer to a question you did not actually ask.

**If the script is green and the wide command is red**, the difference is one of the three
table rows above. Re-run the offending suite unfiltered to see it directly:

```bash
dotnet test tests/Twig.Cli.Tests/Twig.Cli.Tests.csproj --nologo   # no BinaryLauncher filter
```

### Diagnosing an aborted run (#311)

When a run aborts, vstest does **not** name the test that was in flight, and a TRX
does not rescue you — it is written at the end of a run and only describes tests
that COMPLETED, so the in-flight one is absent rather than marked.

`tools/find-hung-test.sh` closes that gap. An assembly-level xUnit hook
(`tests/Shared/TestProgressTrace.cs`) appends a flushed
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
work) cannot explain it on their own.

🔴 **ROOT CAUSE FOUND (ADO #43, 2026-07-31) — it IS `BuildFixture`, and the
reasoning that ruled it out below was wrong.** Preserved so the mistake is not
repeated:

> ~~`BuildFixture`'s nested `dotnet build` is a real cost — reliably the largest
> untraced gap, 2.1 s idle scaling to ~6.2 s under load, 8/8 runs — but it is **not**
> the abort: the abort happened at ~40% of the suite, and the entrypoint tests run
> near the end and were never reached.~~

That inference mapped an abort's **test count onto an execution position** — the
exact move this file forbids two sections above, because xUnit's order is not stable
between runs. It also assumed `AotSmokeTests`'s `Category=Interactive` exclusion
covered the fixture; it does not. `OutputFormatEntrypointTests` shares `BuildFixture`
and is **not** excluded, so the nested build runs on every normal Cli run. The
captured abort hit at 469/3018 (~15%), not ~40%.

**The actual mechanism:** `BuildFixture.RunProcess` timed out only `WaitForExit`,
then blocked on an *untimed, uncancellable* `stdoutTask.GetAwaiter().GetResult()`.
`dotnet build` spawns MSBuild worker nodes and a persistent `VBCSCompiler` that
**outlive the direct child and inherit the redirected stdout handle**. `ReadToEnd`
returns at EOF, and EOF arrives only when the *last* holder closes the write end — so
`WaitForExit` returned promptly while the read blocked forever. The 5-minute timeout
guarded the one call that could never hang. Because this runs in a fixture
**constructor**, xUnit's `CreateClassFixture` never returned and the host stopped
dispatching — producing exactly the "nothing in flight, every START has an END"
signature seen in all seven captures.

Fixed by bounding every wait in `RunProcess` (see its `<remarks>`), with regression
coverage in `BuildFixtureRunProcessTests`. Red-green verified: the hang test fails
against the old implementation and passes against the fixed one.

The evidence *at the boundary* — the vstest host stopping while both processes stay
alive — remains accurate as a **symptom**; it was the fixture blocking upstream of it
all along.

### 🔴 CORRECTION (2026-08-14) — the ADO #43 fix was ONE INSTANCE, not the class

**Issue #311 reproduced in CI**, on PR #397's first run — every prior capture in ~200
attempts was on a local box. Job:

    https://github.com/PolyphonyRequiem/twig/actions/runs/31848823948/job/94920542492

Retrieve the log with (`gh run view --log` returns the *successful* re-run, not this job):

    gh api repos/PolyphonyRequiem/twig/actions/jobs/94920542492/logs

The captured shape:

```
23:02:53  Passed! ... 1998 ... Twig.Domain.Tests.dll
23:02:55  Passed! ... 1496 ... Twig.Infrastructure.Tests.dll
23:02:56  Passed! ...   81 ... Twig.RenderTree.Tests.dll
23:02:57  Passed! ... 1299 ... Twig.Mcp.Tests.dll
23:05:14  Passed! ... 3275 ... Twig.Cli.Tests.dll        <-- COMPLETED, 2m23s
23:07:56  Aborting test run: test run timeout of 300000 milliseconds exceeded.
23:07:56  Passed! ...    9 ... Twig.Tui.Tests.dll        <-- 9 of 85
```

🔴 **The Cli suite COMPLETED and the stall was in `Twig.Tui.Tests`, which has NO
FIXTURES AT ALL** (`grep -rln "IClassFixture\|ICollectionFixture\|IAsyncLifetime"
tests/Twig.Tui.Tests/` returns nothing). `BuildFixture` lives in `Twig.Cli.Tests`. The
suite owning the fixed defect finished normally; the stall moved to an assembly with no
fixture constructor to block in. **The ADO #43 fix is real, verified, and not in
question — but it fixed one instance of a broader class.** Issue #311's closure on the
strength of it was incomplete.

#### Session-budget exhaustion is FALSIFIED — do not raise the timeout

The tempting reading is that CI runs six assemblies in parallel under ONE shared 300 s
session clock, where the local runs gave each suite its own, so this is budget
exhaustion rather than a hang. That reading prescribes the *opposite* action on the
timeout, so it had to be settled first. **The log's own timestamps kill it:**

| | |
|---|---|
| all six hosts start within | **9 s** of each other (23:02:47→23:02:56) |
| Tui host starts | 23:02:56.18 — **300.6 s before the abort** |
| Cli finishes | 23:05:14 — **2m42s BEFORE the abort**, so it crowded out nothing |
| Tui's own reported test-body time | **301 ms** for 9 tests |

Tui held a full ~300 s window and spent 99.9% of it dispatching nothing. Budget
exhaustion predicts a suite guillotined *mid-execution* with progress accumulating up to
the cut; the opposite is observed. This is the **same signature as all three local
captures** — tiny test-body time inside a ~300 s wall, host alive, nothing in flight.

Local control: `Twig.Tui.Tests` runs **85/85 in ~500 ms** standalone on Prime-U.

🔴 Red herring worth naming: the orphan `sleep` pid in that log's cleanup block is
`BuildFixtureRunProcessTests`' deliberate `sleep 3600 &` probe, from the **completed**
Cli suite. It is the ADO #43 regression test working, not the hang.

#### The instrument set was pointed at the wrong assembly — now fixed

Every instrument built for this card (`TestProgressTrace`, `find-hung-test.sh`,
`diag-hunt.sh`, `dispatch-watch.sh`) was **assembly-scoped to Cli**, i.e. aimed at the
suite that finished. That is now corrected:

- `tests/Shared/TestProgressTrace.cs` is **link-compiled into every test assembly** by
  `tests/Directory.Build.props` (guarded on `IsTestProject`). It is deliberately NOT
  hosted in `Twig.TestKit`: TestKit carries no xunit reference, adding one would push
  xunit into every consumer's package graph, and it still would not reach
  `Twig.RenderTree.Tests`, which does not reference TestKit.
- 🔴 **`TWIG_TEST_TRACE` now names a DIRECTORY, not a file.** Each assembly writes
  `<assembly-name>.tsv`. This is mandatory, not cosmetic: CI's single invocation runs
  six assemblies in **parallel processes**, and the hook's in-process lock cannot
  serialise across process boundaries — six hosts appending to one file would interleave
  mid-line and destroy the START/END reconciliation.
- `find-hung-test.sh` reconciles **per assembly**, defaults to CI's wide invocation, and
  narrows with `TWIG_311_SUITE=Cli`.
- CI runs the trace on every PR and uploads `artifacts/trace-311/` on failure.

Verified locally on the wide run: **8234 boundaries against 8234 reported tests, zero
malformed lines, zero interleaving, zero in-flight.** With `TWIG_TEST_TRACE` unset no
files are written and the runtime is unchanged, so the opt-in still costs nothing.

**Hunt in CI, not locally.** CI reproduced on attempt 1 of 2; local rates are ~1 in 11
under heavy load and ~1 in 129 idle. The trace now runs on every PR, so captures
accumulate without a hunt.

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

> 🔴 That blind spot is exactly where the root cause was hiding (see "ROOT CAUSE
> FOUND" above). The watcher could not see the fixture constructor, so it correctly
> reported a boundary-level symptom while the real hang sat upstream of its view.
> The capture's **stacks**, not its gap timing, are what named the cause — always
> read the full `.stack` files, not just the analyzer's filtered "dispatch-relevant
> frames" section, which by design shows only test-platform frames and omitted the
> `Twig.Cli.Tests!BuildFixture..ctor()` line that settled it.

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

### The false-green class: four cards, one shape

AB#350, AB#352, AB#79 and AB#398 are the same defect wearing four costumes — **a check that
looks green while saying nothing, or a refusal that says nothing true**. Worth naming, because
each one was found only after a caller trusted it:

| Card | Surface | What reported success |
|---|---|---|
| AB#350 | `twig new --field a=1 --field b=2` | exit 0 having written one field of two |
| AB#352 | `tools/run-tests.sh --typo` | no verdict line at all, so the mandated grep came back empty |
| AB#79 | `twig link bogus 5` | top-level usage, exit 0, nothing created |
| AB#398 | `twig note hello world` | `Argument 'world' is not recognized` — true, and never naming the fix |

🔴 **AB#398's own CARD carried the defect it was reporting, and that is the transferable
lesson.** It stated the symptom as *"one bare word is accepted, the SECOND is rejected"* and
listed `twig new "hello world" --type Task` among the working spellings. Measured against the
real binary at the card's own commit, **both were false**: `note`, `new` and `seed chain`
declared their trailing words as `params string[]` *after* the `CancellationToken`, a shape for
which ConsoleAppFramework 5.7.13 emits **no positional slot at all**, so `twig note hello` was
rejected too and the documented workaround did not work either. Every probe behind the card had
used the same input arity, so the report generalised from one shape. **Run the same command at
0, 1, 2 and 3 tokens, quoted and unquoted, and read WHICH token the error names** — `'hello'`
and `'world'` are different diagnoses wearing one error string.

The consequence for the remedy was structural, not cosmetic. The card asked for a hint
(`Did you mean: twig note "hello world"`) and that hint would have pointed at a **second
identical failure** — a false green in a helpful tone. So the fix is two halves, in order:
restore the accepted spelling with a single `[Argument]` slot, *then* make the leftover refusal
honest. `src/Twig/Commands/StrayPositionalGuard.cs` owns the second half and must never be
extended to a command whose quoted form is not actually accepted.

**The card's "do not re-derive this" note needed one word of correction, which is why the
distinction is recorded here.** The reverted attempt used `[Argument] string**[]**` — an
*array* slot, which consumes one token and comma-splits it, moving the error from the first
word to the second without fixing anything. A **scalar** `[Argument] string?` is a different
shape, is what `set`/`show`/`update` already use, and works. A do-not-re-derive note is
evidence about the exact thing that was tried; check whether your variant is that thing.

The blast radius was wider than the three commands named: a sweep of the repo's own `--help`
examples against the generated parser found `edit` and `init` documenting positional spellings
their parsers rejected. `tools/positional-drift.py` is that sweep, kept as a guard —
it is red on the pre-fix tree and green after, so it discriminates. Its first run reported
**four** commands and one was `nav`, where `nav up`/`nav down` are real subcommands rather than
positionals; the tokenizer, not the corpus, was wrong. **A high first-run count from a new
corpus-wide checker indicts the checker.**

🔴 **AB#79's audit found the class is worse than the "usage and exit 0" signature suggests,
and an audit hunting only that signature would have missed the worst instance.** What a
mistyped subcommand did depended on the group's bare handler, and none of the three outcomes
was an error:

* **No bare handler** (`link`, `bench`, `auth`, `ohmyposh`, `workspace sprint`) — printed
  top-level usage, exit 0. The reported shape.
* **Bare handler taking no positional** (`nav`, `workspace`, `area`) — the stray word was
  ignored and the BARE command ran. `twig nav bogus` launched the interactive navigator.
* **Bare handler taking a positional** (`seed`) — the stray word was consumed AS that
  positional. `twig seed bogus` printed no usage whatsoever: it **created a seed titled
  "bogus"** and exited 0. A false green covering a real write, which is strictly worse than a
  false green covering a no-op.

The guard is `src/Twig/Commands/SubcommandGuard.cs`. It decides on **structure** — is the
chain so far a group prefix whose bare form legitimately consumes a value — rather than on a
per-verb list, and the two judgement calls that structure cannot make (`PrefixesTakingPositional`,
`PrefixesWhereSubcommandWins`) are hand-maintained and therefore pinned by reflection guards
that fail the build when code and registry disagree. That pinning is not optional ceremony:
a hand-maintained list is exactly how the whole `bench` group shipped unreachable (ADO
#148-150, 3,072 CLI tests green).

**`twig set 123` must keep working.** `set`, `show`, `state` and `note` are not group prefixes
— no known command begins `"set "` — so they never reach the rejection branch, and
`GroupedHelpTests.IsKnownCommand_FallsBackToTopLevelWhenCompoundUnknown` passes unmodified.

**`twig link --help` must keep exiting 0.** AB#352's lesson applies in reverse here: a usage
error for a request that SUCCEEDED is a false RED, and a false red corrodes an exit code just
as surely as a false green.

### Regression tests must fail on the unfixed code

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

### Mutation harnesses

Where a fix's whole value is that its guards are non-vacuous, the mutation run is worth
keeping rather than describing. `tools/ab79-mutants.sh` patches `SubcommandGuard.cs` wrong ten
ways and reports which arms go red **by name**; a `SURVIVED` line means the tests are weaker
than they look.

```bash
tools/ab79-mutants.sh
```

🔴 **A mutant that DOES NOT COMPILE is not a killed mutant.** Three of the first ten came back
`error CS` — dead-code and unreachable-return errors from mutations that were invalid rather
than caught — and the harness reports that as its own outcome instead of banking it as a pass.
Reading a compile error as "the tests caught it" is the same false green the script exists to
measure.

🔴 **Include a mutant that swaps two guards' WORDING while leaving both firing.** Killing a
guard outright is the easy case; the failure AGENTS.md records for `ConflictFixture`, and which
cost two survived mutants on AB#352, is two guards masking each other so the test passes
against a dead one. Assert each guard's distinct message and mutate one into the other's.

## Git

The repo **squash-merges**, so branch SHAs are not ancestors of `main` after merge —
`git merge-base --is-ancestor` returning false is expected, not a failed merge.
Verify content landed instead:

```bash
git show origin/main:path/to/File.cs | grep -c "YourNewSymbol"
```

GitHub auto-close keywords do not chain: `Fixes #253 and #252` closes only #253.
Repeat the keyword — `Fixes #253, fixes #252`.

## Where work is tracked

**Consolidated 2026-08-06.** Three trackers held three layers of the same work and no
single surface answered "what is the state of X". The split is now deliberate:

| What | Lives in | Why |
|---|---|---|
| **Work** — defects, tasks, anything schedulable | **ADO** (`PolyphonyRequiem/Twig`) | One board. This is the source of truth for status and scheduling. |
| **Decisions** — wayfinder rulings, specs | **This repo** (`wayfinder/`, `wayfinder-1.0/`, `docs/specs/`) | They are reviewed with the code they govern, diff cleanly, and carry evidence a work item cannot hold. |
| **Public record** — issues from outside | **GitHub** | Contributors have no ADO access. Issues stay open; tracking moves to ADO. |

**The rule that makes this work is bidirectional linking.** A tracker split without links
just moves the problem — you get two places to look instead of one, and no way to get from
either to the other:

- An ADO item implementing a ruling **names that ruling** in its description.
- A ruling that has been scheduled declares its board items in **frontmatter**:
  `tracked_in: [139]`.
- A GitHub issue migrated to ADO gets a comment naming the ADO item, and the ADO item's
  description opens with the GitHub URL. The issue stays open — closing it would hide a
  live defect from the public.

### Enforcing it

Prose does not hold. This repo already learned that when guidance telling humans to
"remember the exit code" failed often enough to need `tools/run-tests.sh`. So the links
are checked by a script:

```bash
tools/check-tracking.sh              # verify every declared link
tools/check-tracking.sh 1007         # one ticket
tools/check-tracking.sh --selftest   # prove the checker can fail AND pass
```

It asserts both directions: the work item must **resolve** (a dangling id is a hard error,
the same stale-reference class the Bench design rejects elsewhere), and the item's
description must **name the ticket back** (a one-way link leaves you on a board item with
no idea which ruling it implements).

A ticket with no `tracked_in` is **not** an error. Most rulings were never scheduled, and
demanding a board item for each would push ceremony onto the decision layer.

🔴 **The selftest has three arms, two negative and one positive, and that is deliberate.**
Testing only that a guard rejects bad input cannot distinguish a working guard from one
that always fails. The positive arm creates a real work item, verifies the checker accepts
it, then deletes it. Run `--selftest` after touching the script.

Three issues (#333, #357, #359) were migrated this way. Existing commit-message ADO tags
(`AB#nnnn`) are unaffected and remain the commit↔item link.
