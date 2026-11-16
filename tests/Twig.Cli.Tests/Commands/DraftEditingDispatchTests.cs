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
    public void RetiredDraftEditingCommandsAreAbsentAndRefusedBeforeComposition(string verb)
    {
        CommandCatalog.KnownCommands.ShouldNotContain("draft " + verb);
        Twig.CoreMutationAdmissionGuard.ValidateCatalog(["draft", verb]).ShouldNotBeNull();
    }
}
