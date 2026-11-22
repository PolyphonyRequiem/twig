using Shouldly;
using Xunit;

namespace Twig.Server.Tests;

public sealed class CommandScopeTests
{
    [Theory]
    [InlineData("set 42", "show 42")]
    [InlineData("nav up", "tree --id 42")]
    [InlineData("states", "process Task")]
    [InlineData("state Doing", "state Doing --id 42")]
    [InlineData("bench switch Review", "bench rules Review")]
    [InlineData("workspace --refresh", "workspace --bench Review --refresh")]
    [InlineData("workspace sync", "workspace area sync")]
    public void WorkerAdmitsOnlyIndependentInvocations(string refused, string admitted)
    {
        foreach (var output in new[] { "human", "json" })
        {
            ServerCommandWorker.Check([.. refused.Split(' '), "-o", output]).ShouldNotBeNull();
            ServerCommandWorker.Check([.. admitted.Split(' '), "-o", output]).ShouldBeNull();
        }
    }
}
