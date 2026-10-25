using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class DraftEditingDispatchTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("stage")]
    [InlineData("retain")]
    public void DraftEditingCommands_AreListedAndAcceptedByNativeGuard(string verb)
    {
        GroupedHelp.KnownCommands.ShouldContain("draft " + verb);
        SubcommandGuard.Validate(["draft", verb]).ShouldBeNull();
    }
}
