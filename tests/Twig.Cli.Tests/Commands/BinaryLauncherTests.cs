using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class BinaryLauncherTests
{
    [Theory]
    [InlineData("twig-tui", "Twig.Tui")]
    [InlineData("twig-mcp", "Twig.Mcp")]
    public void Launch_BinaryNotFound_ReturnsExitCode1WithDescriptiveError(string binaryName, string projectName)
    {
        using var sw = new StringWriter();
        var exitCode = BinaryLauncher.Launch(binaryName, projectName, sw);

        exitCode.ShouldBe(1);
        var error = sw.ToString();
        error.ShouldContain("not found");
        error.ShouldContain(binaryName);
        error.ShouldContain(projectName);
    }
}
