# AB#524 — the Terminal.Gui module initializer is DEADLOCKED, not starving

**Verdict for AB#524 (Bug, parent AB#390): (b) deadlock.** This document is the
evidence, so a future reader does not re-derive it — and so the falsified
hypothesis (starvation) is not re-tried.

Card: AB#524, project Twig.
Capture under analysis: `/home/polyphonyrequiem/captures/twig-ab390-311-stall/ab390-capture-12/`
Pinned dependency: **Terminal.Gui 2.0.0-develop.5185** (`Directory.Packages.props:26`).

Instruments kept from this investigation, all under `tools/repro-311/`:

| Tool | What it does |
|---|---|
| `ab524-precondition.py` | **deterministic** — computes which assemblies can race the cctor; needs no stall |
| `ab524-split-wide.sh` | the splitting experiment: CI's wide invocation with the session timeout removed |
| `ab524-no-timeout.runsettings` | diagnostic-only runsettings (**never** copy into `test.runsettings`) |
| `ab524-decompile.sh` | reads the pinned Terminal.Gui initializer source |

## Verdict

**(b) DEADLOCK.** The initializer does not complete, and the evidence is not
"it was still going when the timeout fired" — it is "nothing in the process was
running at all."

## The decisive measurement: nothing is on CPU

The starvation hypothesis requires that *some* thread be doing work. It is not.
From the three `/proc` snapshots 30s apart (analysis scripts live beside the
durable capture):

| snap | process state | vol_ctxt | nonvol | cctor thread state | cctor wchan |
|---|---|---|---|---|---|
| 1 | S (sleeping) | 14 | 61 | **S** | `futex_do_wait` |
| 2 | S (sleeping) | 14 | 61 | **S** | `futex_do_wait` |
| 3 | S (sleeping) | 14 | 61 | **S** | `futex_do_wait` |

Whole-process thread census, all three snapshots: **20 threads, 20 in state `S`,
zero in `R`.** Distinct wait channels present are only `futex_do_wait`,
`ep_poll`, `poll_schedule_timeout`, `wait_for_partner` — every one of them a
*blocking* channel.

🔴 This is what kills starvation. A slow reflection walk over loaded assemblies
is CPU-bound work; it would show the initializing thread `R`/running and its
context-switch counters advancing. Instead the thread that is *inside*
`GetDefinedTypes` is itself parked on a futex, and the counters are frozen at
14/61 across 90 seconds. The process is not being starved of CPU — it is not
asking for any.

## Which thread is where

`analyze.py` / `cctor_cycle.py`, consistent across all three snapshots:

- **1 thread** (`0x1A1A3F` / 1710655) inside the cctor:
  `Terminal.Gui!<Module>..cctor -> InitializeConfigurationManager ->
  ConfigurationManager.Initialize -> ConfigProperty.Initialize ->
  RuntimeModule.GetDefinedTypes -> [Native Frames]` — parked on `futex_do_wait`.
- **3 threads** (`0x1A19E9`, `0x1A19EF`, `0x1A1A42`) stopped at
  `MethodBaseInvoker.InterpretedInvoke_Method` under xunit's
  `TestInvoker.CallTestMethod`, with **no managed frames below the invoke**.
  That is the signature of a thread parked on the CLR per-type init lock,
  waiting for the cctor the first thread is stuck in.

1 + 3 = **4**, exactly matching AB#390's "4 tests in flight, 13 started, 9
passed". Same TIDs in all three snapshots — no rotation, no progress.

No cctor *cycle*: only one thread is inside a class constructor, so this is not
two initializers waiting on each other. It is one initializer wedged on a lower
lock while three others queue behind its type-init lock.

## The mechanism, read from the pinned binary

Decompiled from the pinned 2.0.0-develop.5185 assembly
(`tools/repro-311/ab524-decompile.sh`; note the nuget `ilspycmd` is too old for
net10 metadata — install current and point `DOTNET_ROOT` at the preview.5 root):

```csharp
// Terminal.Gui.ModuleInitializers
[ModuleInitializer]
internal static void InitializeConfigurationManager() => ConfigurationManager.Initialize();

// Terminal.Gui.Configuration.ConfigProperty.Initialize()  — lines 348-392
Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();   // 355
foreach (Assembly assembly in assemblies) {
    if (assembly.IsDynamic) continue;
    Type[] types = assembly.GetTypes();                            // 364  <-- wedged here
    foreach (Type type in types) { type.GetProperties(); ... }
}
```

So a **module initializer** — code the CLR runs while holding that module's
type-init lock — performs an unbounded walk of *every assembly loaded in the
process*, forcing full type resolution on each via `GetTypes()`.

`GetTypes()` takes the CLR class-loader lock
(`ClassLoader::LoadTypeHandleForTypeKey` → `CrstBase::Enter`). Meanwhile the
other parallel xunit collections in the same host are themselves loading and
JITting their own test classes, which takes the same loader lock. The result is
a lock-ordering inversion between the per-type init lock (held by the cctor
thread) and the class-loader lock (wanted by it, and held/contended by the
other collections). Nothing breaks the cycle, so nobody runs — which is exactly
the all-threads-sleeping state measured above.

**Prior art, same shape, third party:** dotnet/runtime#10556 — `GetTypes()`
called from a static constructor deadlocking on
`clr!ClassLoader::LoadTypeHandleForTypeKey` against a second thread doing
assembly load. That issue's second party was an external assembly-load hook and
it was closed as a Visual Studio bug; the *mechanism* — reflection-over-all-types
inside a type initializer blocking on the loader lock — is identical to ours,
and it is version-independent CLR behaviour rather than a claim about a
Terminal.Gui version.

**Terminal.Gui's own tracker corroborates the design smell but does NOT carry a
deadlock report.** Searched `tui-cs/Terminal.Gui` for deadlock / hang / module
initializer / static constructor: no matching issue. What does exist:
- **#4367** ("Replace CM with MEC", closed) states plainly that the static
  `ConfigurationManager` architecture is what blocks **test parallelization**.
- **#5242 / #5239 / #5240** are AOT crashes on this same
  `ConfigurationManager.Initialize` module-init path, and #5242's own words are
  *"Module-init is a poor place to do reflection in any case."*
- Their `CONTRIBUTING.md` instructs contributors to keep parallelizable tests
  away from the static `ConfigurationManager`.

So upstream already knows this initializer is hostile to parallel execution.
Nobody upstream has reported the loader-lock deadlock specifically.

## Why the control host is the corroborating evidence

Same capture, same moment, `contenders.py`:

| host | parallelization | threads in a type-load path | outcome |
|---|---|---|---|
| Tui (`host-1710540`) | **enabled** (no attribute) | **4 of 16** | STALLED |
| Cli (`host-1710438`) | `DisableTestParallelization = true` | 1 of 15 | completed normally |

`tests/Twig.Cli.Tests/AssemblyAttributes.cs:9` carries
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
`tests/Twig.Tui.Tests/AssemblyAttributes.cs` carries **no such attribute**, and
the assembly has 5 test classes = 5 parallel collections, **all five of which
can trigger the Terminal.Gui module cctor** — three by naming `Terminal.Gui`
types directly, two (`DetailDocumentSourceTests`, `PendingChangeStoreSinkTests`)
transitively via `Twig.Tui.Views`, which is compiled against Terminal.Gui.
The four tests AB#390 recorded as in flight are in four different classes, and
`PendingChangeStoreSinkTests` — one of the four — is a *transitive* trigger,
which is why a direct-reference grep undercounts. `tools/repro-311/ab524-precondition.py`
computes this and is the deterministic check; it does not depend on catching a stall:

```
Twig.Tui.Tests   ENABLED   5 test classes   5 can trigger the cctor   AT RISK
(every other test assembly: not at risk)
```

Twig.Tui.Tests is the **only** assembly in the repo meeting the precondition,
which is consistent with AB#390's finding that the stall relocated here after
AB#43 fixed the Cli suite's `BuildFixture` instance.

## Load anti-correlates, which starvation does not predict

From `hunt-attempts.log` in the durable capture: the 11 clean attempts ran at
loadavg 29.8–50.0; **the one attempt that captured the stall ran at 25.5, the
lowest of the twelve.** If the walk were merely slow under contention, the
capture should land on the *most* loaded attempts. It landed on the least.
Contention perturbs interleaving (which is why load raises the hit rate at all);
it is not the thing that stops the walk.

## What this rules in and out

- **Do NOT raise `TestSessionTimeout`.** Already falsified by AB#390; this
  analysis strengthens it — with zero threads runnable, more time cannot help.
- **Do NOT "warm the initializer once at assembly init"** as a *cure*. That is
  the starvation-shaped fix. It would help only by accident (fewer racing
  threads at the moment of init), and it still runs the same unbounded
  reflection walk while holding a type-init lock.
- **The mechanism-appropriate mitigation is to stop racing the cctor from
  parallel test threads** — i.e. give `Twig.Tui.Tests` the same
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` the Cli
  suite already has, which is also what Terminal.Gui's own CONTRIBUTING.md
  tells consumers to do. That is a mitigation in *our* repo for a defect in
  *theirs*; the underlying bug is Terminal.Gui's and is worth reporting upstream.

**Shipped** in PR #410 on `fix/524-terminalgui-initializer`, with the attribute's
reasoning written beside it in `AssemblyAttributes.cs` and a mutation-proven guard
(`ParallelizationPolicyTests`) so the one-line mitigation cannot silently regress.

### A guard that was written and then deliberately removed

Worth recording, because it went **green** and would have shipped looking like
coverage. A second test tried to pin the mitigation's *premise* — that every test
class in the assembly can reach Terminal.Gui, which is why the switch is
assembly-wide rather than per-class. Three implementations were attempted, and
each first run indicted the **detector**, not the premise:

1. **Member-signature reflection** reported all five classes unreachable —
   including the three that name Terminal.Gui in their `using` directives. These
   tests construct those objects inside method *bodies* and expose nothing in
   their signatures. Reflection describes shape, not what code touches.
2. **An IL token scan** got it from 5 to 2, still missing the two transitive
   classes: a metadata token is only meaningful at its true instruction offset,
   and guessing offsets is unreliable in both directions.
3. **A source-text search** finally passed — but it would pass on a class that
   merely mentions the name in a comment, and fail on one that reaches
   Terminal.Gui through a helper.

Version 3 is an assertion pointed at the wrong surface. A guard that cannot fail
for the right reason is worse than no guard, because its presence implies a
coverage that does not exist — and three attempts on one assertion is the signal
to question the approach rather than try a fourth. The premise now lives in the
attribute's own comment and in `ab524-precondition.py`, which reads sources
directly and is honest about being a static approximation.

The guard that *remains* claims only that the attribute is present and true, and
is mutation-proven: green restored → killed **by name** under
`DisableTestParallelization = false` with zero compile errors → green restored,
byte-identical to snapshot.

## Limits of this finding, stated honestly

- **The splitting experiment was INCONCLUSIVE, and is recorded as such.** CI's
  wide invocation (8375 tests traced per attempt) was run with the session
  timeout removed, under verified heavy load (loadavg 25–47 on 20 cores,
  per-attempt wall 98–187s against ~11s idle): **14 of 14 attempts clean**, no
  stall reproduced. At the measured ~1-in-12 hit rate that is an ordinary
  outcome with the bug fully present, so it refutes nothing — the experiment can
  only speak when it *catches* a stall. The verdict rests on the capture, not on
  this hunt.
- **A discarded earlier version of that experiment must not be read as
  evidence:** it ran `Twig.Tui.Tests` standalone and returned 14/14 clean at 3–4s
  wall / 85 tests. Wrong shape — the capture comes from the wide invocation at
  ~100s and 8375 tests, and a 3–4s run barely opens the window in which the cctor
  can be raced. It is a valid negative control (the trace hook works, the
  assembly is healthy alone) and nothing more.
- The initializing thread bottoms out in `[Native Frames]`, so the *specific*
  CLR lock it waits on is inferred from (i) the managed frame directly above it
  (`RuntimeModule.GetDefinedTypes`), (ii) the documented behaviour of
  `GetTypes()` taking the class-loader lock, and (iii) the matching native stack
  in dotnet/runtime#10556. It is **not** read directly from this capture's
  native frames. Confirming it would need `dotnet-dump`/lldb on a fresh capture.
- What *is* directly observed, and does not depend on that inference: no thread
  in the process is runnable, the state is identical across 90 seconds, and the
  thread inside the initializer is blocked rather than computing. That alone
  settles (b) over (a).
- A green run cannot prove a timing race absent. Any mitigation must be reported
  with that limit rather than as "fixed, verified by a passing suite."
