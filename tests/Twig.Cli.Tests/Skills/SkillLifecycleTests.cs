using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

public sealed class SkillLifecycleTests : IDisposable
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "twig-skills-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(sandbox, "skills");
    private static SkillPackage Package(string version = "1.0.0+first", bool extra = false)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["twig-cli/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig-cli\ndescription: Twig\n---\n# Twig {{TWIG_VERSION}}\n"),
            ["twig-changes/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig-changes\ndescription: Changes\n---\n# Changes\n"),
            ["twig-cli/references/help.md"] = Encoding.UTF8.GetBytes("Help")
        };
        if (extra) files["twig-cli/references/new.md"] = Encoding.UTF8.GetBytes("New");
        return new SkillPackage(version, files);
    }

    [Theory]
    [InlineData("hermes")]
    [InlineData("omp")]
    [InlineData("copilot")]
    public void Install_is_flat_and_idempotent_for_explicit_provider_target(string provider)
    {
        var service = new SkillLifecycle(Package());
        service.Install(provider, Target).State.ShouldBe("installed");
        var before = File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath));
        service.Install(provider, Target).State.ShouldBe("current");
        File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath)).ShouldBe(before);
        File.ReadAllText(Path.Combine(Target, "twig-cli/SKILL.md")).ShouldContain("1.0.0+first");
        File.Exists(Path.Combine(Target, "twig-changes/SKILL.md")).ShouldBeTrue();
        service.Status(provider, Target).State.ShouldBe("current");
    }

    [Fact]
    public void Target_and_provider_are_explicit_and_status_does_not_create_directories()
    {
        var service = new SkillLifecycle(Package());
        Should.Throw<SkillLifecycleException>(() => service.Install("hermes", "")).Message.ShouldContain("--target");
        Should.Throw<SkillLifecycleException>(() => service.Install("unknown", Target)).Message.ShouldContain("--provider");
        service.Status("hermes", Target).State.ShouldBe("not-installed");
        Directory.Exists(Target).ShouldBeFalse();
    }

    [Fact]
    public void Conflicting_family_name_is_not_adopted_even_when_bytes_match()
    {
        var package = Package();
        Write("twig-cli/SKILL.md", Encoding.UTF8.GetString(package.GetFiles()["twig-cli/SKILL.md"]));
        Should.Throw<SkillLifecycleException>(() => new SkillLifecycle(package).Install("omp", Target)).Message.ShouldContain("conflict");
        File.Exists(Path.Combine(Target, "twig-changes/SKILL.md")).ShouldBeFalse();
    }

    [Fact]
    public void Update_refuses_edited_managed_files_before_writing_anything()
    {
        new SkillLifecycle(Package()).Install("hermes", Target);
        Write("twig-changes/SKILL.md", "user edit");
        var entry = File.ReadAllText(Path.Combine(Target, "twig-cli/SKILL.md"));
        Should.Throw<SkillLifecycleException>(() => new SkillLifecycle(Package("2.0.0+next", true)).Update("hermes", Target))
            .Message.ShouldContain("edited");
        File.ReadAllText(Path.Combine(Target, "twig-cli/SKILL.md")).ShouldBe(entry);
        File.ReadAllText(Path.Combine(Target, "twig-changes/SKILL.md")).ShouldBe("user edit");
        File.Exists(Path.Combine(Target, "twig-cli/references/new.md")).ShouldBeFalse();
    }

    [Fact]
    public void Update_preserves_companions_provider_settings_and_selections()
    {
        var old = new SkillLifecycle(Package());
        old.Install("hermes", Target);
        Write("my-presenter/SKILL.md", "---\nname: my-presenter\ndescription: Mine\n---\nPrivate text");
        Write("unrelated-settings.json", "user settings");
        old.Configure("hermes", Target, "discord", "my-presenter");
        var selection = File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.SelectionPath));
        var next = new SkillLifecycle(Package("2.0.0+next", true));
        next.Status("hermes", Target).State.ShouldBe("stale");
        Should.Throw<SkillLifecycleException>(() => next.Install("hermes", Target)).Message.ShouldContain("update");
        next.Update("hermes", Target).State.ShouldBe("updated");
        File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.SelectionPath)).ShouldBe(selection);
        File.ReadAllText(Path.Combine(Target, "unrelated-settings.json")).ShouldBe("user settings");
        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md")).ShouldContain("Private text");
        next.Status("hermes", Target).State.ShouldBe("current");
    }

    [Fact]
    public void Configure_missing_companion_is_explicit_and_changes_nothing()
    {
        var service = new SkillLifecycle(Package());
        service.Install("copilot", Target);
        var before = File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.SelectionPath));
        Should.Throw<SkillLifecycleException>(() => service.Configure("copilot", Target, "terminal", "missing"))
            .Message.ShouldContain("missing");
        File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.SelectionPath)).ShouldBe(before);
        Should.Throw<SkillLifecycleException>(() => service.Configure("copilot", Target, "terminal", "twig-cli"));
    }

    [Fact]
    public void Missing_selected_companion_is_reported_with_safe_fallback_and_clear_is_supported()
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        Write("my-presenter/SKILL.md", "---\nname: my-presenter\ndescription: Mine\n---\nBody");
        service.Configure("omp", Target, "terminal", "my-presenter");
        File.Delete(Path.Combine(Target, "my-presenter/SKILL.md"));
        var status = service.Status("omp", Target);
        status.Warnings.ShouldContain(w => w.Contains("missing") && w.Contains("approval"));
        service.Configure("omp", Target, "terminal", clear: true);
        service.Status("omp", Target).Warnings.ShouldNotContain(w => w.Contains("missing"));
    }

    [Fact]
    public void Configure_preserves_other_scenarios_and_does_not_edit_companion()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        const string content = "---\nname: my-presenter\ndescription: Mine\n---\nBody";
        Write("my-presenter/SKILL.md", content);
        service.Configure("hermes", Target, "discord", "my-presenter");
        service.Configure("hermes", Target, "terminal", "my-presenter");
        service.Configure("hermes", Target, "terminal", clear: true);
        using var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(Target, SkillLifecycle.SelectionPath)));
        selection.RootElement.GetProperty("selections").GetProperty("hermes").GetProperty("discord").GetString().ShouldBe("my-presenter");
        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md")).ShouldBe(content);
    }

    [Fact]
    public void Explicit_scan_root_reports_same_frontmatter_name_under_different_directory()
    {
        var service = new SkillLifecycle(Package());
        service.Install("copilot", Target);
        var other = Path.Combine(sandbox, "other");
        Directory.CreateDirectory(Path.Combine(other, "different-folder"));
        File.WriteAllText(Path.Combine(other, "different-folder/SKILL.md"), "---\nname: twig-cli\ndescription: Shadow\n---\nBody");
        var status = service.Status("copilot", Target, other);
        status.Warnings.ShouldContain(w => w.Contains("shadow") && w.Contains("different-folder"));
        status.DiscoveryScope.ShouldContain("bounded");
    }

    [Fact]
    public void Provider_mismatch_is_not_silently_retargeted()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        Should.Throw<SkillLifecycleException>(() => service.Update("copilot", Target)).Message.ShouldContain("provider");
    }

    [Fact]
    public void Tampered_manifest_cannot_manage_outside_family_or_escape_target()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var path = Path.Combine(Target, SkillLifecycle.ManifestPath);
        var json = File.ReadAllText(path).Replace("twig-cli/references/help.md", "../outside.txt", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        Should.Throw<SkillLifecycleException>(() => service.Update("hermes", Target));
    }

    [Fact]
    public void Package_identity_changes_with_build_metadata_and_content()
    {
        Package("1.0.0+one").Identity.ShouldNotBe(Package("1.0.0+two").Identity);
        Package(extra: true).Identity.ShouldNotBe(Package().Identity);
        Package().ShowEntry().ShouldContain("1.0.0+first");
    }

    [Fact]
    public void Symlink_target_and_managed_file_are_rejected_without_touching_referent()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink privilege is not assumed.
        Directory.CreateDirectory(sandbox);
        var real = Path.Combine(sandbox, "real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Target, real);
        var service = new SkillLifecycle(Package());
        Should.Throw<SkillLifecycleException>(() => service.Install("hermes", Target)).Message.ShouldContain("symlink");
        Directory.GetFileSystemEntries(real).ShouldBeEmpty();
        Directory.Delete(Target);
        service.Install("hermes", Target);
        var outside = Path.Combine(sandbox, "outside");
        File.WriteAllText(outside, "outside");
        File.Delete(Path.Combine(Target, "twig-cli/SKILL.md"));
        File.CreateSymbolicLink(Path.Combine(Target, "twig-cli/SKILL.md"), outside);
        Should.Throw<SkillLifecycleException>(() => service.Update("hermes", Target)).Message.ShouldContain("symlink");
        File.ReadAllText(outside).ShouldBe("outside");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
    }
}
