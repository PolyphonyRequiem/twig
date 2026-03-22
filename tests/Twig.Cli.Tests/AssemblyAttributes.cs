// Disable test parallelization within this assembly to prevent file system
// races between tests that share temporary directories (InitCommand,
// MultiContextInit, PromptStateWriter).
// NOTE: Console.SetOut/SetError redirections have been eliminated (EPIC-003)
// — commands now accept an injectable TextWriter for stderr.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
