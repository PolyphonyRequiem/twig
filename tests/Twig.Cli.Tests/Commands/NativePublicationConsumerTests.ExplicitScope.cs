using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task PreviousExplicitReadDoesNotSupplyALaterTarget(string output)
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        RunCoreLink(paths, ["show", "42", "--refresh", "-o", output]).ShouldBe(0);
        var requests = _transport.TotalRequests;
        foreach (var args in new string[][] { ["show"], ["tree"], ["note", "--stdin"], ["state", "Doing"],
                     ["seed", "new", "--type", "Task", "--title", "No parent"], ["workspace"], ["bench", "rules"] })
        {
            Twig.TwigConsoleApp.Run([.. args, "-o", output], _ => throw new InvalidOperationException(
                "Missing targets must fail before service composition.")).ShouldBe(2);
        }
        _transport.TotalRequests.ShouldBe(requests);
        _transport.PatchAttempts.ShouldBe(0);
    }
}
