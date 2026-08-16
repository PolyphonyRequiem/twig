// Disable test parallelization within this assembly to prevent file system
// races between tests that share temporary directories (InitCommand,
// MultiContextInit, PromptStateWriter).
// NOTE: Console.SetOut/SetError redirections have been eliminated (EPIC-003)
// — commands now accept an injectable TextWriter for stderr.
using Twig.TestSupport;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// GitHub issue #311 instrumentation. Inert unless TWIG_TEST_TRACE names a DIRECTORY;
// when set, records a flushed START/END line per test into <assembly-name>.tsv so an
// aborted run can be reconciled to the test that was in flight — and so the stalling
// ASSEMBLY is identifiable, which the 2026-08-14 CI capture showed the single-file
// design could not do. Source is tests/Shared/TestProgressTrace.cs, link-compiled into
// every test assembly by tests/Directory.Build.props.
[assembly: TestProgressTrace]
