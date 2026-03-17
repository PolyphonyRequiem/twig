using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for version display (ITEM-118): twig --version outputs version string.
/// </summary>
[Trait("Category", "Interactive")]
public class VersionDisplayTests : IClassFixture<BuildFixture>
{
    // Matches a SemVer version: major.minor.patch with optional pre-release suffix
    private static readonly Regex SemVerPattern = new(@"^\d+\.\d+\.\d+(-[\w.]+)?$", RegexOptions.Compiled);

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
        SemVerPattern.IsMatch(stdout.Trim()).ShouldBeTrue(
            $"Expected a valid SemVer version but got: '{stdout.Trim()}'");
    }

    [Fact]
    public void DotnetRun_VersionCommand_ReturnsVersionString()
    {
        _build.BuildSucceeded.ShouldBeTrue("Build must succeed before run tests");

        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"run --project \"{_build.ProjectPath}\" --no-build -- version", timeoutMinutes: 2);

        exited.ShouldBeTrue("Process timed out after 2 minutes");
        exitCode.ShouldBe(0, $"version command failed with stderr: {stderr}");
        SemVerPattern.IsMatch(stdout.Trim()).ShouldBeTrue(
            $"Expected a valid SemVer version but got: '{stdout.Trim()}'");
    }

}
