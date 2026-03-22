using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TuiLauncherTests
{
    [Fact]
    public void Launch_BinaryNotFound_ReturnsExitCode1()
    {
        // TuiLauncher.Launch() will fail to find twig-tui (not built/on PATH in test env)
        using var sw = new StringWriter();
        var exitCode = TuiLauncher.Launch(sw);

        exitCode.ShouldBe(1);
        sw.ToString().ShouldContain("not found");
    }

    [Fact]
    public void Launch_ErrorMessage_IncludesBinaryName()
    {
        using var sw = new StringWriter();
        TuiLauncher.Launch(sw);
        sw.ToString().ShouldContain("twig-tui");
    }
}
