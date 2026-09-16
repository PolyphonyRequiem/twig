using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

/// <summary>
/// Command-surface behaviour tests for `twig skills`. The public command class is parameterless
/// by design so it can route before startup side effects; these tests drive the internal
/// <see cref="SkillCommands.Run(string, TextWriter, TextWriter, Func{SkillLifecycle, SkillLifecycleResult})"/>
/// seam so behaviour, exit codes and machine-parseable output are asserted directly, without
/// touching Console handles or ConsoleAppFramework's argument binder.
/// </summary>
public sealed class SkillCommandsTests
{
    private static SkillLifecycleResult Success() => new(
        State: "installed",
        Provider: "hermes",
        Target: "/tmp/example",
        PackageIdentity: "1.0.0+first:sha256:deadbeef",
        InstalledIdentity: "1.0.0+first:sha256:deadbeef",
        Warnings: Array.Empty<string>());

    [Fact]
    public void Human_output_is_plain_text_and_exits_zero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = SkillCommands.Run("human", stdout, stderr, _ => Success());
        code.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
        var text = stdout.ToString();
        text.ShouldContain("Twig skills: installed");
        text.ShouldContain("Provider: hermes");
        text.ShouldContain("Target: /tmp/example");
        text.ShouldContain("1.0.0+first:sha256:deadbeef");
    }

    [Fact]
    public void Json_output_is_parseable_and_carries_provider_hint()
    {
        var stdout = new StringWriter();
        var code = SkillCommands.Run("json", stdout, new StringWriter(), _ => Success());
        code.ShouldBe(0);
        var payload = JsonSerializer.Deserialize(stdout.ToString(), SkillJsonContext.Default.SkillCommandOutput)!;
        payload.State.ShouldBe("installed");
        payload.Provider.ShouldBe("hermes");
        payload.Target.ShouldBe("/tmp/example");
        payload.PackageIdentity.ShouldBe("1.0.0+first:sha256:deadbeef");
        payload.ProviderHint.ShouldNotBeNullOrWhiteSpace();
        payload.DiscoveryScope.ShouldContain("Discovery check");
        payload.Externals.ShouldBeEmpty();
        payload.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Minimal_output_is_tagged_tab_separated_rows_that_preserve_every_correctness_surface()
    {
        var result = new SkillLifecycleResult(
            State: "current",
            Provider: "omp",
            Target: "/tmp/example",
            PackageIdentity: "2.0.0+next:sha256:cafe",
            InstalledIdentity: "1.0.0+first:sha256:beef",
            Warnings: new[] { "Potential shadowing for 'my-presenter'" })
        {
            Selections = new Dictionary<string, string> { ["discord"] = "my-presenter" },
            Externals = new[] { "hermes-discord-adapter" }
        };
        var stdout = new StringWriter();
        var code = SkillCommands.Run("minimal", stdout, new StringWriter(), _ => result);
        code.ShouldBe(0);
        var rows = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        // Every row is `tag\tvalue`. State, identities and target are always emitted so a
        // consumer can never lose the correctness surface by choosing minimal.
        rows.ShouldContain("state\tcurrent");
        rows.ShouldContain("provider\tomp");
        rows.ShouldContain("target\t/tmp/example");
        rows.ShouldContain("bundled\t2.0.0+next:sha256:cafe");
        rows.ShouldContain("installed\t1.0.0+first:sha256:beef");
        rows.ShouldContain("selection\tdiscord\tmy-presenter");
        rows.ShouldContain("external\thermes-discord-adapter");
        rows.ShouldContain("warning\tPotential shadowing for 'my-presenter'");
    }

    [Fact]
    public void Minimal_no_selections_or_externals_still_prints_identity_and_state_rows()
    {
        var stdout = new StringWriter();
        var code = SkillCommands.Run("minimal", stdout, new StringWriter(), _ => Success());
        code.ShouldBe(0);
        var text = stdout.ToString();
        text.ShouldContain("state\tinstalled");
        text.ShouldContain("bundled\t1.0.0+first:sha256:deadbeef");
        text.ShouldContain("installed\t1.0.0+first:sha256:deadbeef");
    }

    [Fact]
    public void Ids_is_rejected_before_action_with_usage_exit_code()
    {
        var stderr = new StringWriter();
        var called = false;
        var code = SkillCommands.Run("ids", new StringWriter(), stderr, _ => { called = true; return Success(); });
        code.ShouldBe(OutputFormatArgumentValidator.UsageExitCode);
        called.ShouldBeFalse();
        stderr.ToString().ShouldContain("not supported");
    }

    [Fact]
    public void Unknown_output_format_is_rejected_before_action_matching_repo_convention()
    {
        var stderr = new StringWriter();
        var called = false;
        var code = SkillCommands.Run("bogus", new StringWriter(), stderr, _ => { called = true; return Success(); });
        code.ShouldBe(OutputFormatArgumentValidator.UsageExitCode);
        called.ShouldBeFalse();
        stderr.ToString().ShouldContain("Unknown output format 'bogus'");
    }

    [Fact]
    public void Human_error_emits_recovery_hint_and_exits_one()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = SkillCommands.Run("human", stdout, stderr,
            _ => throw new SkillLifecycleException("Simulated failure"));
        code.ShouldBe(1);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldContain("Twig skills: Simulated failure");
        stderr.ToString().ShouldContain("No force overwrite is available");
    }

    [Fact]
    public void Json_error_is_parseable_envelope_on_stderr()
    {
        var stderr = new StringWriter();
        var code = SkillCommands.Run("json", new StringWriter(), stderr,
            _ => throw new SkillLifecycleException("Boom"));
        code.ShouldBe(1);
        var envelope = JsonSerializer.Deserialize(stderr.ToString(), SkillJsonContext.Default.SkillCommandErrorOutput)!;
        envelope.Error.ShouldBe("Boom");
        envelope.Recovery.ShouldContain("No force overwrite");
    }

    [Fact]
    public void Minimal_error_emits_tagged_error_and_recovery_rows_on_stderr()
    {
        var stderr = new StringWriter();
        var code = SkillCommands.Run("minimal", new StringWriter(), stderr,
            _ => throw new SkillLifecycleException("Terse"));
        code.ShouldBe(1);
        var rows = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        rows.ShouldContain("error\tTerse");
        rows[1].ShouldStartWith("recovery\t");
    }

    [Fact]
    public void Json_full_and_json_compact_use_the_same_source_gen_envelope_as_json()
    {
        foreach (var format in new[] { "json", "json-full", "json-compact" })
        {
            var stdout = new StringWriter();
            SkillCommands.Run(format, stdout, new StringWriter(), _ => Success()).ShouldBe(0);
            JsonSerializer.Deserialize(stdout.ToString(), SkillJsonContext.Default.SkillCommandOutput)
                .ShouldNotBeNull();
        }
    }
}
