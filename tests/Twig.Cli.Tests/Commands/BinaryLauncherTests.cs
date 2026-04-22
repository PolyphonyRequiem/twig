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
        // Override PATH so FindInPath cannot accidentally discover an installed companion
        // binary (e.g., from ~/.twig/bin). This makes the test environment-independent.
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            using var sw = new StringWriter();
            var exitCode = BinaryLauncher.Launch(binaryName, projectName, sw);

            exitCode.ShouldBe(1);
            var error = sw.ToString();
            error.ShouldContain("not found");
            error.ShouldContain(binaryName);
            error.ShouldContain(projectName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
