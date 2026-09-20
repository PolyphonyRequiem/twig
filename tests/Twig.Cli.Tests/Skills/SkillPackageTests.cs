using System.Reflection;
using System.Text;
using Shouldly;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

public sealed class SkillPackageTests
{
    [Fact]
    public void Embedded_package_contains_canonical_entry_and_references_bound_to_full_assembly_build()
    {
        var package = SkillPackage.Load();
        var fullVersion = typeof(SkillPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        package.Version.ShouldBe(fullVersion);
        package.Identity.ShouldStartWith(fullVersion + ":sha256:");
        package.GetFiles().Keys.ShouldContain("twig/SKILL.md");
        package.GetFiles().Keys.ShouldContain("twig/references/operations.md");
        package.GetFiles().Keys.ShouldContain("twig/references/changes.md");
        package.GetFiles().Keys.ShouldContain("twig/references/presentation.md");
        package.GetFiles().Keys.ShouldNotContain("twig-cli/SKILL.md");
        package.GetFiles().Keys.ShouldNotContain("twig-changes/SKILL.md");
        package.GetFiles().Keys.ShouldNotContain(SkillLifecycle.SelectionPath);
        package.ShowEntry().ShouldContain(package.Identity);
        package.ShowEntry().ShouldContain("name: twig");
        package.ShowReference("operations").ShouldContain("# Operating Twig");
        package.ShowReference("changes").ShouldContain("# Changing tracker work");
        package.ShowReference("presentation").ShouldContain("# Presenting Twig information");
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
    [InlineData("twig/../../outside.md")]
    [InlineData("other/SKILL.md")]
    [InlineData("twig//bad.md")]
    [InlineData("twig/evil\\outside.md")]
    [InlineData("twig/file:stream")]
    [InlineData("twig/CON.txt")]
    [InlineData("twig/references/trailing.")]
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
        files["twig/skill.md"] = Encoding.UTF8.GetBytes("alias");
        Should.Throw<SkillLifecycleException>(() => new SkillPackage("test", files));
    }

    [Fact]
    public void Package_requires_canonical_entry()
    {
        var files = Files();
        files.Remove("twig/SKILL.md");
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
        ["twig/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig\ndescription: Twig\n---\nBody"),
        ["twig/references/operations.md"] = Encoding.UTF8.GetBytes("Operations"),
        ["twig/references/changes.md"] = Encoding.UTF8.GetBytes("Changes"),
        ["twig/references/presentation.md"] = Encoding.UTF8.GetBytes("Presentation")
    };
}
