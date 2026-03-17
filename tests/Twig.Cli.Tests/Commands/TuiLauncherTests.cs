using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TuiLauncherTests
{
    [Fact]
    public void Launch_BinaryNotFound_ReturnsExitCode1()
    {
        // TuiLauncher.Launch() will fail to find twig-tui (not built/on PATH in test env)
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var exitCode = TuiLauncher.Launch();

            exitCode.ShouldBe(1);
            sw.ToString().ShouldContain("not found");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void Launch_ErrorMessage_IncludesBinaryName()
    {
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            TuiLauncher.Launch();
            sw.ToString().ShouldContain("twig-tui");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
