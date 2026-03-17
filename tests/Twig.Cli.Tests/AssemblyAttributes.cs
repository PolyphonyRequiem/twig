// Disable test parallelization within this assembly to prevent Console.SetOut
// redirections in CommandFormatterWiringTests from capturing output from other
// test classes running concurrently.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
