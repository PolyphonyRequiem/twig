using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for <see cref="GlobalProfilePaths"/>: path structure, sanitization,
/// and correct filenames for status-fields and metadata.
/// </summary>
public class GlobalProfilePathsTests
{
    [Fact]
    public void GetProfileDir_ReturnsExpectedPathStructure()
    {
        var result = GlobalProfilePaths.GetProfileDir("myorg", "Agile");

        result.ShouldContain(".twig");
        result.ShouldContain("profiles");
        result.ShouldContain("myorg");
        result.ShouldContain("Agile");
        result.ShouldEndWith(Path.Combine("profiles", "myorg", "Agile"));
    }

    [Fact]
    public void GetProfileDir_SanitizesUnsafeCharacters()
    {
        var result = GlobalProfilePaths.GetProfileDir("org/bad", "process:evil");

        result.ShouldContain("org_bad");
        result.ShouldContain("process_evil");
        result.ShouldNotContain("/bad");
        result.ShouldNotContain(":evil");
    }

    [Fact]
    public void GetStatusFieldsPath_AppendsCorrectFilename()
    {
        var result = GlobalProfilePaths.GetStatusFieldsPath("myorg", "Agile");

        result.ShouldEndWith(Path.Combine("myorg", "Agile", "status-fields"));
    }

    [Fact]
    public void GetMetadataPath_AppendsCorrectFilename()
    {
        var result = GlobalProfilePaths.GetMetadataPath("myorg", "Agile");

        result.ShouldEndWith(Path.Combine("myorg", "Agile", "profile.json"));
    }

    [Fact]
    public void GetStatusFieldsPath_IsInsideProfileDir()
    {
        var profileDir = GlobalProfilePaths.GetProfileDir("myorg", "Scrum");
        var statusFieldsPath = GlobalProfilePaths.GetStatusFieldsPath("myorg", "Scrum");

        statusFieldsPath.ShouldStartWith(profileDir);
    }

    [Fact]
    public void GetMetadataPath_IsInsideProfileDir()
    {
        var profileDir = GlobalProfilePaths.GetProfileDir("myorg", "Scrum");
        var metadataPath = GlobalProfilePaths.GetMetadataPath("myorg", "Scrum");

        metadataPath.ShouldStartWith(profileDir);
    }
}
