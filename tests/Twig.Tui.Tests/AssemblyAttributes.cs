// GitHub issue #311 instrumentation. Inert unless TWIG_TEST_TRACE names a DIRECTORY;
// when set, records a flushed START/END line per test into <assembly-name>.tsv so an
// aborted run can be reconciled to the test that was in flight — and so the stalling
// ASSEMBLY is identifiable.
//
// Added assembly-wide (not just to Cli) because the 2026-08-14 CI capture showed the
// Cli suite COMPLETING normally at 3275 tests while Twig.Tui.Tests stalled at 9 of 85.
// Every prior instrument was scoped to Cli and was therefore pointed at the wrong
// assembly. Source is tests/Shared/TestProgressTrace.cs, link-compiled by
// tests/Directory.Build.props.
using Twig.TestSupport;
using Xunit;

// AB#524 — DO NOT REMOVE WITHOUT READING
// docs/research/terminal-gui-module-initializer-deadlock.md.
//
// This is a mitigation in OUR repo for a defect in Terminal.Gui (pinned
// 2.0.0-develop.5185). Terminal.Gui's [ModuleInitializer] runs
// ConfigProperty.Initialize, which walks AppDomain.CurrentDomain.GetAssemblies()
// and calls GetTypes() on every one — an unbounded, type-loading reflection walk
// executed while the CLR holds that module's type-init lock. GetTypes() takes the
// CLR class-loader lock, which other parallel xunit collections in the same host
// also need in order to load their own test classes. That inversion deadlocks.
//
// Captured 2026-08-16 (AB#390, attempt 12/12): 20 threads, ALL sleeping, ZERO
// runnable; one thread inside the cctor blocked in GetDefinedTypes, three parked on
// the per-type init lock behind it; voluntary_ctxt_switches frozen at 14/14/14
// across 90s. That is a deadlock, not a slow walk — so more time cannot help and
// raising TestSessionTimeout is NOT the fix.
//
// All five test classes in this assembly can trigger that initializer (three name
// Terminal.Gui types directly; DetailDocumentSourceTests and
// PendingChangeStoreSinkTests reach it transitively through Twig.Tui.Views), so
// without this attribute five collections race one module cctor. Terminal.Gui's own
// CONTRIBUTING.md tells consumers to keep parallelizable tests away from the static
// ConfigurationManager, and their issue #4367 states that this architecture is what
// blocks test parallelization.
//
// tools/repro-311/ab524-precondition.py recomputes the risk and is the check to run
// if this assembly gains classes or the pin moves. Retire this only when Terminal.Gui
// stops doing reflection at module-init time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

[assembly: TestProgressTrace]
