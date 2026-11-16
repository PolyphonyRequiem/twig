using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class CurrentCliCatalogTests
{
    [Theory]
    [InlineData("tree", "--no-live")]
    [InlineData("workspace", "--flat")]
    [InlineData("workspace", "--no-live")]
    [InlineData("process", "--refresh")]
    [InlineData("seed publish", "--force")]
    [InlineData("proposal preview", "--full")]
    [InlineData("proposal preview", "--width")]
    [InlineData("proposal preview", "--color")]
    public void GeneratedHelpDoesNotAdvertiseIgnoredOptions(string command, string option)
    {
        var runner = Twig.TwigConsoleApp.CreateRunner(_ => { });
        var output = ProgressiveHelp.CaptureGeneratedHelp(runner, [.. command.Split(' '), "--help"]);
        output.ShouldContain("Usage:");
        output.ShouldNotContain(option);
    }

    [Theory]
    [InlineData("tree", "--no-live")]
    [InlineData("workspace", "--flat")]
    [InlineData("process", "--refresh")]
    [InlineData("seed publish", "--force")]
    [InlineData("proposal preview", "--full")]
    [InlineData("proposal preview", "--interactive")]
    [InlineData("proposal preview", "--include-rendering")]
    [InlineData("plan preview", "--file")]
    [InlineData("area add", "Project")]
    public void RetiredRequestsRefuseBeforeComposition(string command, string option)
    {
        var composed = false;
        var exit = Twig.TwigConsoleApp.Run([.. command.Split(' '), option, "-o", "json"], _ => composed = true);
        exit.ShouldBe(1);
        composed.ShouldBeFalse();
    }
}
