using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Wiring guards specific to the plan and pending surfaces. The bulk of registration and
/// help-text completeness is enforced by <see cref="GroupedHelpTests"/> and
/// <see cref="Twig.Cli.Tests.DependencyInjection.CommandRegistrationCompletenessTests"/>;
/// these lock the promises native to the plan lifecycle CLI that the general guards would
/// happily let drift.
/// </summary>
public sealed class PlanCliRegistrationTests
{
    [Fact]
    public async Task PlanPreview_LegacyPublicSignatureStillForwardsFileFormatAndCancellation()
    {
        var method = typeof(TwigCommands).GetMethod(nameof(TwigCommands.PlanPreview),
            [typeof(string), typeof(string), typeof(CancellationToken)]);
        method.ShouldNotBeNull("existing three-argument C# callers must retain their public overload");
        method.ReturnType.ShouldBe(typeof(Task<int>));

        using var cancellation = new CancellationTokenSource();
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("legacy.json", cancellation.Token).Returns(new PlanPreviewResult
        {
            Digest = "legacy-digest", CanApply = true, Operations = [], Issues = [], PendingChanges = [],
        });
        var output = new StringWriter();
        var command = new PlanCommand(lifecycle, new(new HumanOutputFormatter()),
            new UnresolvedSessionSteeringModeProvider(), TimeProvider.System, stdout: output);
        using var services = new ServiceCollection().AddSingleton(command).BuildServiceProvider();
        var commands = new TwigCommands(services);
        var result = (Task<int>)method.Invoke(commands, ["legacy.json", "json", cancellation.Token])!;
        (await result).ShouldBe(0);
        // Compile the original method-group shape as well as looking up its runtime signature.
        Func<string?, string, CancellationToken, Task<int>> legacy = commands.PlanPreview;
        output.GetStringBuilder().Clear();
        (await legacy("legacy.json", "json", cancellation.Token)).ShouldBe(0);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("digest").GetString().ShouldBe("legacy-digest");
        await lifecycle.Received(2).PreviewAsync("legacy.json", cancellation.Token);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
        // CAF registers only the declared preview handler; the inherited legacy overload
        // must remain a callable C# method, not a second CLI verb.
        typeof(TwigCommands).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(TwigCommands.PlanPreview)).ShouldHaveSingleItem();
        method.DeclaringType.ShouldNotBe(typeof(TwigCommands));
    }

    [Theory]
    [InlineData("proposal")]
    [InlineData("plan")]
    public async Task PreviewProductionCommand_HasDensityOptionsAndNoCompatibilityVerb(string verb)
    {
        var result = await RunPreviewCliAsync(verb, "preview", "--help");
        result.ExitCode.ShouldBe(0, result.Stderr);
        result.Stdout.ShouldContain("Usage: proposal preview|plan preview");
        result.Stdout.ShouldContain("--file");
        result.Stdout.ShouldContain("-o, --output");
        result.Stdout.ShouldContain("--full");
        result.Stdout.ShouldContain("--interactive");
        var unknown = await RunPreviewCliAsync("plan-preview", "--help");
        unknown.ExitCode.ShouldBe(1);
        unknown.Stderr.ShouldContain("Unknown command: 'plan-preview'");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPreviewCliAsync(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var assembly = Path.Combine(root, "src", "Twig", "bin", configuration, "net11.0", "twig.dll");
        File.Exists(assembly).ShouldBeTrue();
        var scratch = Directory.CreateTempSubdirectory("twig-preview-registration-");
        try
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = scratch.FullName, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            start.ArgumentList.Add(assembly);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
            using var process = Process.Start(start);
            process.ShouldNotBeNull();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { scratch.Delete(recursive: true); }
    }

    /// <summary>
    /// The proposal (canonical) and legacy plan spellings, plus <c>pending</c>, must
    /// resolve as top-level compound commands. Defends against dropping either alias
    /// half from <see cref="GroupedHelp.KnownCommands"/>, which would silently break
    /// grouped help routing and the <see cref="SubcommandGuard"/> unknown-subcommand
    /// diagnostic for the missing spelling.
    /// </summary>
    [Theory]
    [InlineData("proposal")]
    [InlineData("proposal validate")]
    [InlineData("proposal preview")]
    [InlineData("proposal apply")]
    [InlineData("proposal status")]
    [InlineData("proposal seed")]
    [InlineData("plan")]
    [InlineData("plan validate")]
    [InlineData("plan preview")]
    [InlineData("plan apply")]
    [InlineData("plan status")]
    [InlineData("plan seed")]
    [InlineData("pending")]
    public void KnownCommands_IncludesProposalSurface(string command)
    {
        GroupedHelp.KnownCommands.ShouldContain(command);
    }

    /// <summary>
    /// The <c>plan apply</c> handler MUST accept a <c>confirm</c> option — the shared
    /// contract routes the digest through <c>--confirm</c>, and the MCP tool mirrors that
    /// spelling. If this parameter is renamed the CLI still compiles but the two surfaces
    /// silently disagree.
    /// </summary>
    [Fact]
    public void PlanApply_HasConfirmParameter()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.PlanApply),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull();
        method.GetParameters().ShouldContain(p => p.Name == "confirm");
        method.GetParameters().ShouldContain(p => p.Name == "file");
    }

    // Defends against silently splitting the canonical/alias pair when [Command] is
    // edited: if either side of the pipe drops or gets reordered (alias-first), the
    // canonical verb disappears from the CLI or `plan <verb>` stops routing.
    [Theory]
    [InlineData(nameof(TwigCommands.PlanValidate), "proposal validate|plan validate")]
    [InlineData(nameof(TwigCommands.PlanPreview), "proposal preview|plan preview")]
    [InlineData(nameof(TwigCommands.PlanApply), "proposal apply|plan apply")]
    [InlineData(nameof(TwigCommands.PlanStatus), "proposal status|plan status")]
    [InlineData(nameof(TwigCommands.PlanSeed), "proposal seed|plan seed")]
    [InlineData(nameof(TwigCommands.Pending), "pending")]
    public void PlanHandler_HasExpectedCommandAttribute(string methodName, string expected)
    {
        var method = typeof(TwigCommands).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        method.ShouldNotBeNull();
        var cmdAttr = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
        cmdAttr.ShouldNotBeNull($"{methodName} must carry [Command] so ConsoleAppFramework routes it as '{expected}'.");
        var name = (string)cmdAttr.ConstructorArguments[0].Value!;
        name.ShouldBe(expected);
    }

    // Defends against regressing either half of the piped attribute: a refactor that
    // rewrites `[Command("proposal <verb>|plan <verb>")]` back to a single spelling
    // would still compile and pass the exact-string test above (if updated) while
    // silently breaking the other alias for every user. This test decomposes the
    // attribute string on `|` and pins that BOTH the canonical `proposal <verb>` and
    // the legacy `plan <verb>` names appear, for all five verbs.
    [Theory]
    [InlineData(nameof(TwigCommands.PlanValidate), "proposal validate", "plan validate")]
    [InlineData(nameof(TwigCommands.PlanPreview), "proposal preview", "plan preview")]
    [InlineData(nameof(TwigCommands.PlanApply), "proposal apply", "plan apply")]
    [InlineData(nameof(TwigCommands.PlanStatus), "proposal status", "plan status")]
    [InlineData(nameof(TwigCommands.PlanSeed), "proposal seed", "plan seed")]
    public void PlanHandler_CommandAttribute_RegistersBothCanonicalAndAlias(
        string methodName,
        string canonical,
        string alias)
    {
        var method = typeof(TwigCommands).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        method.ShouldNotBeNull();

        var cmdAttr = method.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
        cmdAttr.ShouldNotBeNull();
        var raw = (string)cmdAttr.ConstructorArguments[0].Value!;

        var names = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Canonical MUST be listed first: ConsoleAppFramework uses the leading name as
        // the primary spelling in generated help, and this ticket ratified `proposal`
        // as canonical with `plan` demoted to alias.
        names.Length.ShouldBeGreaterThanOrEqualTo(2,
            $"{methodName} [Command] must register both '{canonical}' and '{alias}'.");
        names[0].ShouldBe(canonical,
            $"{methodName} [Command] must list '{canonical}' first; '{alias}' is the deprecated alias.");
        names.ShouldContain(alias,
            $"{methodName} [Command] must retain the legacy '{alias}' spelling as an alias.");
    }

    // Defends against a stray `[Hidden]` sneaking onto the proposal surface — it is the
    // canonical verb set, not a deprecated path, and must remain visible in grouped help.
    [Theory]
    [InlineData(nameof(TwigCommands.PlanValidate))]
    [InlineData(nameof(TwigCommands.PlanPreview))]
    [InlineData(nameof(TwigCommands.PlanApply))]
    [InlineData(nameof(TwigCommands.PlanStatus))]
    [InlineData(nameof(TwigCommands.PlanSeed))]
    [InlineData(nameof(TwigCommands.Pending))]
    public void PlanHandler_IsNotHidden(string methodName)
    {
        var method = typeof(TwigCommands).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        method.ShouldNotBeNull();
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeFalse($"{methodName} must be visible in grouped help — the plan surface is not deprecated.");
    }
}
