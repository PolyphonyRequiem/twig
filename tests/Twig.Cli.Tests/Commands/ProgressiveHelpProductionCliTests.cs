using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Exercises the production CLI entry point, not a replacement parser.
/// These tests never build or publish a second binary.
/// </summary>
public sealed class ProgressiveHelpProductionCliTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "twig-help-" + Guid.NewGuid().ToString("N"));

    public ProgressiveHelpProductionCliTests()
    {
        Directory.CreateDirectory(_scratch);
        // Discovery/configuration must not run for help, even in a broken workspace.
        Directory.CreateDirectory(Path.Combine(_scratch, ".twig"));
        File.WriteAllText(Path.Combine(_scratch, ".twig", "config"), "not valid JSON");
    }

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Root_IsShortTaskOrientedAndOffersCompleteDiscovery(string spelling)
    {
        var result = await RunTwig(spelling);
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain("Usage: twig");
        result.Stdout.ShouldContain("Read and find");
        result.Stdout.ShouldContain("--help-all");
        result.Stdout.ShouldContain("twig <command> --help");
        result.Stdout.Length.ShouldBeLessThan(2400);
        result.Stdout.ShouldNotContain("link unrelate");
        result.Stdout.ShouldNotContain("workspace sprint remove");
    }

    [Fact]
    public async Task FullCatalog_IsAvailableWithoutExpandingRoot()
    {
        var result = await RunTwig("--help-all");
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain("link unrelate");
        result.Stdout.ShouldContain("workspace sprint remove");
        result.Stdout.ShouldContain("proposal apply");
        result.Stdout.ShouldContain("ohmyposh init");
    }

    [Theory]
    [InlineData("seed", "seed publish", "bench create")]
    [InlineData("proposal", "proposal apply", "seed publish")]
    [InlineData("link", "link related", "proposal apply")]
    [InlineData("bench", "bench delete", "link parent")]
    [InlineData("auth", "auth login", "seed publish")]
    [InlineData("workspace sprint", "workspace sprint remove", "workspace area add")]
    [InlineData("workspace area", "workspace area add", "workspace sprint remove")]
    [InlineData("ohmyposh", "ohmyposh init", "seed publish")]
    [InlineData("skills", "skills install", "seed publish")]
    public async Task Group_ShowsOnlyItsOwnCommands(string group, string included, string excluded)
    {
        var result = await RunTwig([.. group.Split(' '), "--help"]);
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain(included);
        result.Stdout.ShouldNotContain(excluded);
        result.Stdout.ShouldContain("--help-all");
    }

    [Fact]
    public async Task GroupWithBareHandler_PreservesItsOptionsAndOffersChildren()
    {
        var result = await RunTwig("process", "--help");
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("--include-hidden");
        result.Stdout.ShouldContain("process layout");
        result.Stdout.ShouldNotContain("seed publish");
    }

    [Theory]
    [InlineData("show", "--refresh", "twig show 1234", "pending writes")]
    [InlineData("proposal apply", "--authorize", "twig proposal apply --file", "Digest is a hard gate")]
    [InlineData("seed publish", "--dry-run", "twig seed publish --all", "## Exit codes and failure modes")]
    public async Task Leaf_KeepsGeneratedOptionsDefaultsExamplesAndCanonicalEffects(
        string command, string option, string example, string behavior)
    {
        var result = await RunTwig([.. command.Split(' '), "--help"]);
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain("Usage:");
        result.Stdout.ShouldContain("Options:");
        result.Stdout.ShouldContain(option);
        result.Stdout.ShouldContain("Default:");
        result.Stdout.ShouldContain("Examples:");
        result.Stdout.ShouldContain(example);
        result.Stdout.ShouldContain("Behavior and effects");
        result.Stdout.ShouldContain("Exit codes and failure modes");
        result.Stdout.ShouldContain(behavior);
    }

    [Fact]
    public async Task HelpBeforeAndAfterCommand_AreEquivalent()
    {
        var before = await RunTwig("help", "workspace", "area", "add");
        var after = await RunTwig("workspace", "area", "add", "--help");
        before.ExitCode.ShouldBe(0);
        after.ExitCode.ShouldBe(0);
        before.Stdout.ShouldBe(after.Stdout);
    }

    [Fact]
    public async Task LeafHelpWithArguments_IsStillOfflineAndDoesNotExecute()
    {
        var result = await RunTwig("delete", "123", "--force", "--help");
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain("--force");
        result.Stdout.ShouldContain("Examples:");
        Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories)
            .ShouldBe([Path.Combine(_scratch, ".twig", "config")]);
    }

    [Fact]
    public async Task UnknownCommand_FailsCompactlyWithoutCatalogDump()
    {
        var result = await RunTwig("bogus-command");
        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("Unknown command: 'bogus-command'");
        result.Stderr.ShouldContain("twig --help");
        result.Stderr.ShouldContain("twig --help-all");
        // No group headings: the catalog dump was the failure mode this test locks out.
        result.Stderr.ShouldNotContain("Getting Started:");
        result.Stderr.ShouldNotContain("Workspace:");
        result.Stderr.ShouldNotContain("Proposals:");
        result.Stdout.ShouldNotContain("Commands:");
        // Ceiling defends against a future regression that pastes the catalog back in.
        (result.Stderr.Length + result.Stdout.Length).ShouldBeLessThan(400);
    }

    [Theory]
    [InlineData("help", "nonexistent")]
    [InlineData("proposal", "nonexistent", "--help")]
    [InlineData("workspace", "area", "nonexistent", "--help")]
    public async Task UnknownHelpTopic_IsAnErrorRatherThanSuccessfulParentHelp(params string[] args)
    {
        var result = await RunTwig(args);
        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldBeEmpty();
        result.Stderr.ShouldContain("nonexistent");
        result.Stderr.Length.ShouldBeLessThan(400);
    }

    [Theory]
    [InlineData("There")]
    [InlineData("on", "reinstall")]
    public async Task MultilineSummary_IsNotAnExecutableHelpTopic(params string[] topic)
    {
        var result = await RunTwig(["help", .. topic]);
        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldBeEmpty();
        result.Stderr.ShouldContain("Unknown command:");
        result.Stderr.Length.ShouldBeLessThan(400);
    }

    [Fact]
    public async Task EveryDeclaredCommandAndAlias_HasOfflineHelpWithoutStartupWrites()
    {
        var catalog = await RunTwig("--help-all");
        foreach (var (type, prefix) in new[]
        {
            (typeof(TwigCommands), ""),
            (typeof(Twig.Commands.SkillCommands), "skills "),
            (typeof(Twig.Commands.OhMyPoshCommands), "ohmyposh "),
        })
        {
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var attribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
                var hidden = method.CustomAttributes.Any(a => a.AttributeType.Name == "HiddenAttribute");
                var names = attribute is null ? method.Name.ToLowerInvariant() : (string)attribute.ConstructorArguments[0].Value!;
                foreach (var name in names.Split('|'))
                {
                    var command = prefix + name;
                    // Retired hidden methods that the existing dispatch guard excludes
                    // (such as save) are not reachable compatibility aliases.
                    if (hidden && !GroupedHelp.KnownCommands.Contains(command)) continue;
                    var result = await RunTwig([.. command.Split(' '), "--help"]);
                    result.ExitCode.ShouldBe(0, command);
                    result.Stderr.ShouldBeEmpty(command);
                    result.Stdout.ShouldContain("Usage:", customMessage: command);
                    if (hidden)
                        catalog.Stdout.Split('\n').ShouldNotContain(line => line.StartsWith("  " + command + "  ", StringComparison.Ordinal));
                }
            }
        }
        // Broken config, no credentials, dead proxies: help must bypass startup,
        // including for hidden compatibility aliases, without creating a database/cache.
        Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories)
            .ShouldBe([Path.Combine(_scratch, ".twig", "config")]);
    }

    [Fact]
    public async Task SkillEntry_IsAvailableWithoutWorkspaceInitialization()
    {
        var result = await RunTwig("--skill");
        result.ExitCode.ShouldBe(0);
        result.Stderr.ShouldBeEmpty();
        result.Stdout.ShouldContain("name: twig-cli");
        result.Stdout.ShouldContain("twig-changes");
        result.Stdout.ShouldContain(Twig.Skills.SkillPackage.Load().Identity);
        Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories)
            .ShouldBe([Path.Combine(_scratch, ".twig", "config")]);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunTwig(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var assembly = Path.Combine(root, "src", "Twig", "bin", configuration, "net11.0", "twig.dll");
        File.Exists(assembly).ShouldBeTrue($"Twig CLI assembly not found at {assembly}");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(host) ? "dotnet" : host)
        {
            WorkingDirectory = _scratch,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(assembly);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["HOME"] = _scratch;
        info.Environment["USERPROFILE"] = _scratch;
        info.Environment["XDG_CONFIG_HOME"] = _scratch;
        info.Environment.Remove("TWIG_PAT");
        info.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            info.Environment[key] = "http://127.0.0.1:1";
        info.Environment["NO_PROXY"] = "";
        info.Environment["no_proxy"] = "";
        using var process = Process.Start(info);
        process.ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
