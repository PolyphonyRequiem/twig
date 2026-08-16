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

[assembly: TestProgressTrace]
