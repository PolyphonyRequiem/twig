using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for version display (ITEM-118): twig --version outputs version string.
/// </summary>
[Trait("Category", "Interactive")]
public class VersionDisplayTests
{
    // Matches a SemVer version: major.minor.patch with optional pre-release suffix
    private static readonly Regex SemVerPattern = new(@"^\d+\.\d+\.\d+(-[\w.]+)?$", RegexOptions.Compiled);


    [Fact]
    public void DotnetRun_VersionFlag_ReturnsVersionString()
    {
        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"\"{typeof(TwigCommands).Assembly.Location}\" --version", timeoutMinutes: 2);

        exited.ShouldBeTrue("Process timed out after 2 minutes");
        exitCode.ShouldBe(0, $"--version flag failed with stderr: {stderr}");
        SemVerPattern.IsMatch(stdout.Trim()).ShouldBeTrue(
            $"Expected a valid SemVer version but got: '{stdout.Trim()}'");
    }

    [Fact]
    public void DotnetRun_VersionCommand_ReturnsVersionString()
    {
        var (stdout, stderr, exitCode, exited) = BuildFixture.RunProcess(
            "dotnet", $"\"{typeof(TwigCommands).Assembly.Location}\" version", timeoutMinutes: 2);

        exited.ShouldBeTrue("Process timed out after 2 minutes");
        exitCode.ShouldBe(0, $"version command failed with stderr: {stderr}");
        SemVerPattern.IsMatch(stdout.Trim()).ShouldBeTrue(
            $"Expected a valid SemVer version but got: '{stdout.Trim()}'");
    }

}
