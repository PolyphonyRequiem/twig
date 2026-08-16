using Shouldly;
using Twig.Infrastructure.Config;
using Twig.ProcessOverrides;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#216: unit coverage for the <c>--org</c>/<c>--project</c> precedence decision.
/// </summary>
/// <remarks>
/// 🔴 These do NOT substitute for <see cref="ProcessOverrideProductionCliTests"/>. They pin the
/// decision LOGIC; only the production-CLI tests can observe whether the flags reach it at all,
/// and this card exists because they did not. Both layers are required — a green suite here is
/// exactly what the pre-fix tree would also have produced.
/// </remarks>
public sealed class ProcessOverrideResolverTests
{
    [Fact]
    public void NoFlags_UsesTheWorkspace()
    {
        var decision = ProcessOverrideResolver.Resolve(null, null, Config("Org", "Proj"));

        decision.IsWorkspace.ShouldBeTrue();
        decision.IsOverride.ShouldBeFalse();
        decision.Error.ShouldBeNull();
    }

    [Fact]
    public void BothFlags_WithNoWorkspace_UsesTheOverride()
    {
        var decision = ProcessOverrideResolver.Resolve("OtherOrg", "OtherProj", workspaceConfig: null);

        decision.IsOverride.ShouldBeTrue();
        decision.Org.ShouldBe("OtherOrg");
        decision.Project.ShouldBe("OtherProj");
        decision.Error.ShouldBeNull();
    }

    [Fact]
    public void BothFlags_WithAnUnconfiguredWorkspace_UsesTheOverride()
    {
        // Outside a workspace the CLI still resolves a TwigConfiguration; its coordinates are
        // empty. That must not read as a manifest to conflict with.
        var decision = ProcessOverrideResolver.Resolve("OtherOrg", "OtherProj", Config("", ""));

        decision.IsOverride.ShouldBeTrue();
    }

    [Theory]
    [InlineData("OtherOrg", "Proj", "OtherOrg", "Org")]
    [InlineData("Org", "OtherProj", "OtherProj", "Proj")]
    public void ConflictingFlags_AreRefused_NamingBothValues(
        string org, string project, string flagValue, string manifestValue)
    {
        var decision = ProcessOverrideResolver.Resolve(org, project, Config("Org", "Proj"));

        decision.Error.ShouldNotBeNull();
        decision.IsWorkspace.ShouldBeFalse();
        decision.IsOverride.ShouldBeFalse();
        decision.Error.ShouldContain(flagValue);
        decision.Error.ShouldContain(manifestValue);
        decision.Error.ShouldContain("The manifest is authoritative");
    }

    /// <summary>
    /// Matching flags are not a conflict, and resolve to the cheaper workspace path.
    /// </summary>
    /// <remarks>
    /// 🔴 Case-insensitive, matching <c>InitCommand.GetManifestCoordinateConflict</c>'s
    /// <c>OrdinalIgnoreCase</c> comparison. A case-sensitive comparison here would refuse
    /// <c>--org polyphonyrequiem</c> against a manifest saying <c>PolyphonyRequiem</c> — a
    /// false RED, and a divergence from the rule this deliberately follows.
    /// </remarks>
    [Theory]
    [InlineData("Org", "Proj")]
    [InlineData("ORG", "PROJ")]
    [InlineData("org", "proj")]
    public void FlagsMatchingTheManifest_UseTheWorkspace(string org, string project)
    {
        var decision = ProcessOverrideResolver.Resolve(org, project, Config("Org", "Proj"));

        decision.IsWorkspace.ShouldBeTrue();
        decision.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData("OnlyOrg", null, "--org", "--project")]
    [InlineData(null, "OnlyProj", "--project", "--org")]
    public void HalfAnOverride_IsRefused_NamingTheMissingFlag(
        string? org, string? project, string supplied, string missing)
    {
        var decision = ProcessOverrideResolver.Resolve(org, project, workspaceConfig: null);

        decision.Error.ShouldNotBeNull();
        decision.Error.ShouldContain(supplied);
        decision.Error.ShouldContain(missing);

        // Distinct from the conflict refusal — a half override is a usage error, not a
        // manifest disagreement, and collapsing the two would misdirect the user.
        decision.Error.ShouldNotContain("The manifest is authoritative");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void WhitespaceOnlyFlags_CountAsAbsent(string blank)
    {
        var decision = ProcessOverrideResolver.Resolve(blank, blank, Config("Org", "Proj"));

        decision.IsWorkspace.ShouldBeTrue();
    }

    [Fact]
    public void OverrideValues_AreTrimmed()
    {
        var decision = ProcessOverrideResolver.Resolve("  Org  ", "  Proj  ", workspaceConfig: null);

        decision.Org.ShouldBe("Org");
        decision.Project.ShouldBe("Proj");
    }

    private static TwigConfiguration Config(string org, string project) => new()
    {
        Organization = org,
        Project = project,
    };
}
