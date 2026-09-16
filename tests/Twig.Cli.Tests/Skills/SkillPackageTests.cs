using System.Reflection;
using System.Text;
using Shouldly;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

public sealed class SkillPackageTests
{
    [Fact]
    public void Embedded_package_contains_both_entries_and_is_bound_to_full_assembly_build()
    {
        var package = SkillPackage.Load();
        var fullVersion = typeof(SkillPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        package.Version.ShouldBe(fullVersion);
        package.Identity.ShouldStartWith(fullVersion + ":sha256:");
        package.GetFiles().Keys.ShouldContain("twig-cli/SKILL.md");
        package.GetFiles().Keys.ShouldContain("twig-changes/SKILL.md");
        package.GetFiles().Keys.ShouldNotContain(SkillLifecycle.SelectionPath);
        package.ShowEntry().ShouldContain(package.Identity);
        foreach (var entry in package.GetFiles().Where(p => p.Key.EndsWith("/SKILL.md", StringComparison.Ordinal)))
        {
            var text = Encoding.UTF8.GetString(entry.Value);
            text.ShouldStartWith("---");
            text.ShouldContain(fullVersion);
            text.ShouldNotContain("{{TWIG_VERSION}}");
        }
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("twig-cli/../../outside.md")]
    [InlineData("other/SKILL.md")]
    [InlineData("twig-cli//bad.md")]
    [InlineData("twig-cli/evil\\outside.md")]
    [InlineData("twig-cli/file:stream")]
    [InlineData("twig-cli/CON.txt")]
    [InlineData("twig-cli/references/trailing.")]
    public void Package_rejects_nonportable_and_escaping_paths(string badPath)
    {
        var files = Files();
        files[badPath] = Encoding.UTF8.GetBytes("bad");
        Should.Throw<SkillLifecycleException>(() => new SkillPackage("test", files));
    }

    [Fact]
    public void Package_rejects_case_aliases_that_would_collide_on_windows()
    {
        var files = Files();
        files["twig-cli/skill.md"] = Encoding.UTF8.GetBytes("alias");
        Should.Throw<SkillLifecycleException>(() => new SkillPackage("test", files));
    }

    [Fact]
    public void Package_requires_both_flat_family_entries()
    {
        var files = Files();
        files.Remove("twig-changes/SKILL.md");
        Should.Throw<SkillLifecycleException>(() => new SkillPackage("test", files)).Message.ShouldContain("incomplete");
    }

    [Fact]
    public void Package_cannot_ship_dynamic_user_selection_reference()
    {
        var files = Files();
        files[SkillLifecycle.SelectionPath] = Encoding.UTF8.GetBytes("{}");
        Should.Throw<SkillLifecycleException>(() => new SkillPackage("test", files)).Message.ShouldContain("configure-managed");
    }

    private static Dictionary<string, byte[]> Files() => new()
    {
        ["twig-cli/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig-cli\ndescription: Twig\n---\nBody"),
        ["twig-changes/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig-changes\ndescription: Changes\n---\nBody")
    };
}
