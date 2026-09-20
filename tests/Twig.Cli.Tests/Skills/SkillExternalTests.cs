using System.Text;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

/// <summary>
/// Fixture-only behaviour tests for external-skill add. Every scenario runs against a temporary
/// sandbox — no live hermes/omp/copilot loader is exercised, and no host precedence is claimed.
/// </summary>
public sealed class SkillExternalTests : IDisposable
{
    private readonly string sandbox = CanonicalTempRoot.Create("twig-external-");
    private string Target => Path.Combine(sandbox, "skills");
    private string SourceRoot => Path.Combine(sandbox, "sources");

    private static SkillPackage Package(string version = "1.0.0+first")
    {
        var files = new Dictionary<string, byte[]>
        {
            ["twig/SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: twig\ndescription: Twig\n---\n# Twig\n"),
            ["twig/references/operations.md"] = Encoding.UTF8.GetBytes("Operations"),
        };
        return new SkillPackage(version, files);
    }

    private string StageSource(string name, string body = "Body", (string RelPath, string Text)[]? extras = null)
    {
        var dir = Path.Combine(SourceRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: External\n---\n{body}");
        foreach (var extra in extras ?? Array.Empty<(string, string)>())
        {
            var full = Path.Combine(dir, extra.RelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, extra.Text);
        }
        return dir;
    }

    [Fact]
    public void External_add_installs_from_local_source_and_returns_current_on_reinstall()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var source = StageSource("my-presenter", "Body one", extras: [("references/notes.md", "Notes")]);

        var first = service.AddExternal("hermes", Target, source);
        first.State.ShouldBe("added");
        first.Externals.ShouldContain("my-presenter");
        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md")).ShouldContain("Body one");
        File.ReadAllText(Path.Combine(Target, "my-presenter/references/notes.md")).ShouldBe("Notes");

        // Reinstall with unchanged source is a no-op verdict.
        service.AddExternal("hermes", Target, source).State.ShouldBe("current");
    }

    [Fact]
    public void External_add_refuses_collision_with_untracked_directory()
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var source = StageSource("my-presenter");
        Directory.CreateDirectory(Path.Combine(Target, "my-presenter"));
        File.WriteAllText(Path.Combine(Target, "my-presenter", "SKILL.md"), "user-authored");

        Should.Throw<SkillLifecycleException>(() => service.AddExternal("omp", Target, source))
            .Message.ShouldContain("conflict");
        File.ReadAllText(Path.Combine(Target, "my-presenter", "SKILL.md")).ShouldBe("user-authored");
    }

    [Fact]
    public void External_add_refuses_different_revision_and_does_not_overwrite()
    {
        var service = new SkillLifecycle(Package());
        service.Install("copilot", Target);
        var source = StageSource("my-presenter", "Body one");
        service.AddExternal("copilot", Target, source);

        // Edit the source so its sourceIdentity differs from the tracked revision.
        File.WriteAllText(Path.Combine(source, "SKILL.md"),
            "---\nname: my-presenter\ndescription: External\n---\nBody two");
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("copilot", Target, source))
            .Message.ShouldContain("different revision");
        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md")).ShouldContain("Body one");
    }

    [Fact]
    public void External_add_refuses_reinstall_when_installed_bytes_were_edited()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var source = StageSource("my-presenter");
        service.AddExternal("hermes", Target, source);

        File.WriteAllText(Path.Combine(Target, "my-presenter/SKILL.md"),
            "---\nname: my-presenter\ndescription: External\n---\nHand-edited by user");
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, source))
            .Message.ShouldContain("edited");
        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md")).ShouldContain("Hand-edited by user");
    }

    [Fact]
    public void Base_update_preserves_externals_and_does_not_verify_their_edited_bytes()
    {
        var oldService = new SkillLifecycle(Package("1.0.0+first"));
        oldService.Install("omp", Target);
        var source = StageSource("my-presenter");
        oldService.AddExternal("omp", Target, source);

        // User hand-edits the external after add; a family upgrade must not fail on that edit.
        File.WriteAllText(Path.Combine(Target, "my-presenter/SKILL.md"),
            "---\nname: my-presenter\ndescription: External\n---\nUser edit that base upgrade must preserve");

        var nextService = new SkillLifecycle(Package("2.0.0+next"));
        nextService.Update("omp", Target).State.ShouldBe("updated");

        File.ReadAllText(Path.Combine(Target, "my-presenter/SKILL.md"))
            .ShouldContain("User edit that base upgrade must preserve");
        // Status reports the external as-installed.
        nextService.Status("omp", Target).Externals.ShouldContain("my-presenter");
    }

    [Fact]
    public void External_add_rejects_source_without_skill_md()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var dir = Path.Combine(SourceRoot, "no-skill-md");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"), "Notes");
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, dir))
            .Message.ShouldContain("SKILL.md");
    }

    [Theory]
    [InlineData("twig")]
    [InlineData("twig-cli")]
    [InlineData("twig-changes")]
    public void External_add_rejects_canonical_and_retired_family_names(string familyName)
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var familyNamed = Path.Combine(SourceRoot, "family-clash");
        Directory.CreateDirectory(familyNamed);
        File.WriteAllText(Path.Combine(familyNamed, "SKILL.md"),
            $"---\nname: {familyName}\ndescription: Would clash\n---\nBody");
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("omp", Target, familyNamed))
            .Message.ShouldContain("external skill name");
    }

    [Fact]
    public void External_add_rejects_unnamed_frontmatter()
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var noName = Path.Combine(SourceRoot, "no-name");
        Directory.CreateDirectory(noName);
        File.WriteAllText(Path.Combine(noName, "SKILL.md"), "---\ndescription: nameless\n---\nBody");
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("omp", Target, noName))
            .Message.ShouldContain("name");
    }

    [Fact]
    public void External_add_preserves_hidden_support_files_and_refuses_symlinked_source()
    {
        var service = new SkillLifecycle(Package());
        service.Install("copilot", Target);
        var source = StageSource("my-presenter", extras: [(".resources/config.json", "{\"required\":true}"), ("real.md", "kept")]);

        service.AddExternal("copilot", Target, source).State.ShouldBe("added");
        File.ReadAllText(Path.Combine(Target, "my-presenter/real.md")).ShouldBe("kept");
        File.ReadAllText(Path.Combine(Target, "my-presenter/.resources/config.json")).ShouldBe("{\"required\":true}");

        if (OperatingSystem.IsWindows()) return; // Windows symlink privilege is not assumed.
        var real = StageSource("real-external", body: "real body");
        var linked = Path.Combine(SourceRoot, "linked-external");
        Directory.CreateSymbolicLink(linked, real);
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("copilot", Target, linked))
            .Message.ShouldContain("symlink");
    }

    [Fact]
    public void External_add_requires_current_family_installation()
    {
        var service = new SkillLifecycle(Package());
        var source = StageSource("my-presenter");

        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, source))
            .Message.ShouldContain("install");

        service.Install("hermes", Target);
        var stale = new SkillLifecycle(Package("2.0.0+next"));
        Should.Throw<SkillLifecycleException>(() => stale.AddExternal("hermes", Target, source))
            .Message.ShouldContain("stale");
    }

    [Fact]
    public void External_source_directory_cycle_is_rejected_before_any_installation()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var source = StageSource("cyclic");
        Directory.CreateSymbolicLink(Path.Combine(source, "loop"), source);
        var manifest = File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath));
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("omp", Target, source));
        Directory.Exists(Path.Combine(Target, "cyclic")).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath)).ShouldBe(manifest);
    }

    [Fact]
    public void External_source_case_alias_is_refused_on_case_sensitive_filesystems()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;
        var service = new SkillLifecycle(Package());
        service.Install("copilot", Target);
        var source = StageSource("ambiguous", extras: [("data.json", "first"), ("DATA.json", "second")]);
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("copilot", Target, source));
        Directory.Exists(Path.Combine(Target, "ambiguous")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("---\nname: unusable\n---\nbody")]
    [InlineData("---\nname: unusable\ndescription: Example\nbody without a closing delimiter")]
    public void Undiscoverable_external_metadata_is_refused_before_copying(string content)
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var source = StageSource("unusable");
        File.WriteAllText(Path.Combine(source, "SKILL.md"), content);
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("omp", Target, source));
        Directory.Exists(Path.Combine(Target, "unusable")).ShouldBeFalse();
    }

    [Fact]
    public void External_name_collision_under_another_directory_is_refused()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var source = StageSource("same-name");
        var other = Path.Combine(Target, "different-directory");
        Directory.CreateDirectory(other);
        File.Copy(Path.Combine(source, "SKILL.md"), Path.Combine(other, "SKILL.md"));
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, source));
        Directory.Exists(Path.Combine(Target, "same-name")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("name: harmless\n\"name\": twig-cli")]
    [InlineData("name: harmless\n'na\u006de': my-presenter")]
    [InlineData("name: harmless\n\"na\\u006de\": twig-changes")]
    [InlineData("name: harmless\n? name\n: my-presenter")]
    [InlineData("name: harmless\n<<: {name: twig-cli}")]
    [InlineData("name: !custom harmless")]
    [InlineData("name: &identity harmless\nother: *identity")]
    public void Ambiguous_yaml_identity_is_rejected_before_external_copy(string metadata)
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var source = StageSource("harmless");
        File.WriteAllText(Path.Combine(source, "SKILL.md"), $"---\n{metadata}\ndescription: External\n---\nBody");
        var manifest = File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath));
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, source));
        Directory.Exists(Path.Combine(Target, "harmless")).ShouldBeFalse();
        File.ReadAllBytes(Path.Combine(Target, SkillLifecycle.ManifestPath)).ShouldBe(manifest);
    }

    [Theory]
    [InlineData("\"name\": my-presenter", "description: \"Quoted: description\"")]
    [InlineData("? name\n: my-presenter", "description: >-\n  Multiline\n  description")]
    public void Semantic_yaml_names_participate_in_bounded_collision_detection(string name, string description)
    {
        var service = new SkillLifecycle(Package());
        service.Install("omp", Target);
        var source = StageSource("my-presenter");
        File.WriteAllText(Path.Combine(source, "SKILL.md"), $"---\n{name}\n{description}\n---\nBody");
        service.AddExternal("omp", Target, source).State.ShouldBe("added");
        var scan = Path.Combine(sandbox, "scan");
        Directory.CreateDirectory(Path.Combine(scan, "other-directory"));
        File.Copy(Path.Combine(source, "SKILL.md"), Path.Combine(scan, "other-directory", "SKILL.md"));
        service.Status("omp", Target, scan).Warnings.ShouldContain(w => w.Contains("shadowing"));
        Should.Throw<SkillLifecycleException>(() => service.Configure("omp", Target, "review", "my-presenter", scanRoot: scan));
    }

    [Fact]
    public void Bounded_discovery_rejects_ambiguous_metadata_instead_of_reporting_clean_status()
    {
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var scan = Path.Combine(sandbox, "scan", "other-directory");
        Directory.CreateDirectory(scan);
        File.WriteAllText(Path.Combine(scan, "SKILL.md"),
            "---\nname: harmless\n\"name\": twig-cli\ndescription: External\n---\nBody");
        Should.Throw<SkillLifecycleException>(() => service.Status("hermes", Target, Path.GetDirectoryName(scan)));
    }

    [Fact]
    public async Task External_script_executes_without_privileged_bits_and_mode_drift_is_reported()
    {
        if (OperatingSystem.IsWindows()) return; // Unix modes do not describe Windows execution/ACLs.
        var service = new SkillLifecycle(Package());
        service.Install("hermes", Target);
        var source = StageSource("script-companion", extras: [("scripts/check.sh", "#!/bin/sh\nprintf 'installed-script-ok'\n")]);
        var sourceScript = Path.Combine(source, "scripts/check.sh");
        var safeMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(sourceScript, safeMode | UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit);

        service.AddExternal("hermes", Target, source).State.ShouldBe("added");
        var installed = Path.Combine(Target, "script-companion/scripts/check.sh");
        File.GetUnixFileMode(installed).ShouldBe(safeMode);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process.ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.ShouldBe(0);
        (await stdout).ShouldBe("installed-script-ok");
        (await stderr).ShouldBeEmpty();
        service.AddExternal("hermes", Target, source).State.ShouldBe("current");

        File.SetUnixFileMode(installed, safeMode & ~UnixFileMode.UserExecute);
        service.Status("hermes", Target).Warnings.ShouldContain(w => w.Contains("executable mode"));
        Should.Throw<SkillLifecycleException>(() => service.AddExternal("hermes", Target, source))
            .Message.ShouldContain("executable mode");
        new SkillLifecycle(Package("2.0.0+next")).Update("hermes", Target).State.ShouldBe("updated");
        File.GetUnixFileMode(installed).ShouldBe(safeMode & ~UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
    }
}
