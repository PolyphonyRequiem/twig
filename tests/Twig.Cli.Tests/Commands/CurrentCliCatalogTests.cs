using ConsoleAppFramework;
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
        using var output = new StringWriter();
        var previous = ConsoleApp.Log;
        try
        {
            ConsoleApp.Log = line => output.WriteLine(line);
            runner([.. command.Split(' '), "--help"]);
        }
        finally { ConsoleApp.Log = previous; }
        output.ToString().ShouldContain("Usage:");
        output.ToString().ShouldNotContain(option);
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
