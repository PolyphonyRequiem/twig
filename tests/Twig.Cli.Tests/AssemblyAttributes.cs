// Disable test parallelization within this assembly to prevent file system
// races between tests that share temporary directories (InitCommand,
// MultiContextInit, PromptStateWriter).
// NOTE: Console.SetOut/SetError redirections have been eliminated (EPIC-003)
// — commands now accept an injectable TextWriter for stderr.
using Twig.Cli.Tests.TestSupport;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// twig#311 instrumentation. Inert unless TWIG_TEST_TRACE names a file; when set,
// records a flushed START/END line per test so an aborted run can be reconciled to
// the test that was in flight. See TestSupport/TestProgressTrace.cs.
[assembly: TestProgressTrace]
