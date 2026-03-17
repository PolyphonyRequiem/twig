using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for version display (ITEM-118): twig --version outputs version string.
/// </summary>
[Trait("Category", "Interactive")]
public class VersionDisplayTests : IClassFixture<BuildFixture>
{
    private readonly BuildFixture _build;

    public VersionDisplayTests(BuildFixture build)
    {
        _build = build;
    }

    [Fact]
    public void DotnetRun_VersionFlag_ReturnsVersionString()
    {
        _build.BuildSucceeded.ShouldBeTrue("Build must succeed before run tests");

        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"run --project \"{_build.ProjectPath}\" --no-build -- --version", timeoutMinutes: 2);

        exited.ShouldBeTrue("Process timed out after 2 minutes");
        exitCode.ShouldBe(0, $"--version flag failed with stderr: {stderr}");
        stdout.Trim().ShouldBe("0.1.0");
    }

    [Fact]
    public void DotnetRun_VersionCommand_ReturnsVersionString()
    {
        _build.BuildSucceeded.ShouldBeTrue("Build must succeed before run tests");

        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"run --project \"{_build.ProjectPath}\" --no-build -- version", timeoutMinutes: 2);

        exited.ShouldBeTrue("Process timed out after 2 minutes");
        exitCode.ShouldBe(0, $"version command failed with stderr: {stderr}");
        stdout.Trim().ShouldBe("0.1.0");
    }

}
