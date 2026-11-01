using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class InitCommandProductionCliTests : IDisposable
{
    private readonly string _root = CanonicalTempRoot.Create("twig-native-init-");

    [Fact]
    public async Task ApphostExistingMarkerRefusesThroughInitWithoutRuntimeGateOrStartupEffects()
    {
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue();
        var twig = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twig);
        var marker = Path.Combine(twig, "core-workspace.json");
        await File.WriteAllTextAsync(marker, "preserve incomplete native marker");
        var bytes = await File.ReadAllBytesAsync(marker);
        var home = Path.Combine(_root, "must-not-create-native-home");
        var assembly = typeof(TwigCommands).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (var arg in new[] { "init", "fixture", "Project", "-o", "json" }) start.ArgumentList.Add(arg);
        start.Environment["TWIG_USER_HOME"] = home;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        process.ExitCode.ShouldBe(1, await stderr);
        (await stdout).ShouldBeEmpty();
        using var error = JsonDocument.Parse(await stderr);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("existing");
        Directory.Exists(home).ShouldBeFalse();
        (await File.ReadAllBytesAsync(marker)).ShouldBe(bytes);
        Directory.GetFiles(twig).ShouldBe([marker]);
    }

    [Theory]
    [InlineData("partial", 1)]
    [InlineData("legacy", 1)]
    [InlineData("marker", 1)]
    [InlineData("existing-db", 1)]
    [InlineData("sqlite-evidence", 1)]
    [InlineData("relative", 2)]
    [InlineData("missing-parent", 2)]
    [InlineData("directory", 2)]
    [InlineData("bootstrap-file", 2)]
    [InlineData("provider", 2)]
    [InlineData("coordinates", 2)]
    [InlineData("conflicting-coordinates", 2)]
    [InlineData("force", 2)]
    [InlineData("reinitialize", 2)]
    [InlineData("sprint", 2)]
    [InlineData("area", 2)]
    [InlineData("profile", 2)]
    [InlineData("ids", 2)]
    [InlineData("github-account", 2)]
    public void PreflightRefusesWithoutResolvingCredentialsOrChangingFiles(string scenario, int expected)
    {
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue();
        var args = new List<string> { "init", "fixture", "Project", "-o", "json" };
        var twig = Path.Combine(_root, ".twig");
        switch (scenario)
        {
            case "partial": Directory.CreateDirectory(twig); break;
            case "legacy":
            case "marker":
                Directory.CreateDirectory(twig);
                File.WriteAllText(Path.Combine(twig, scenario == "legacy" ? "twig.db" : "core-workspace.json"), "preserve uncertain evidence");
                break;
            case "existing-db":
            case "sqlite-evidence":
                var file = Path.Combine(_root, "selected.db");
                File.WriteAllText(scenario == "existing-db" ? file : file + "-wal", "preserve database evidence");
                args.AddRange(["--core-store", file]);
                break;
            case "relative": args.AddRange(["--core-store", "relative.db"]); break;
            case "missing-parent": args.AddRange(["--core-store", Path.Combine(_root, "missing", "new.db")]); break;
            case "directory": args.AddRange(["--core-store", _root]); break;
            case "bootstrap-file": args.AddRange(["--core-store", Path.Combine(_root, "twig.json")]); break;
            case "provider": args.AddRange(["--provider", "unknown"]); break;
            case "coordinates": args.RemoveRange(1, 2); break;
            case "conflicting-coordinates": args.AddRange(["--org", "different"]); break;
            case "force":
            case "reinitialize": args.Add("--" + scenario); break;
            case "sprint":
            case "area":
            case "profile": args.AddRange(["--" + scenario, "retired"]); break;
            case "ids": args[^1] = "ids"; break;
            case "github-account":
                args.RemoveRange(1, 2);
                args.AddRange(["--provider", "github", "--host", "github.com", "--repository", "owner/repo"]);
                break;
        }
        var before = Snapshot();
        var beforeDirectory = Directory.Exists(twig);
        var nativeResolutions = 0;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            TwigConsoleApp.Run(args.ToArray(), services =>
            {
                services.AddSingleton<IConnectionBindingService>(_ =>
                {
                    nativeResolutions++;
                    throw new InvalidOperationException("Preflight must not resolve native credentials or create a system registry.");
                });
                services.AddTwigWorkspaceServices(_root, args, Path.Combine(_root, "display.json"));
                services.Any(s => s.ServiceType == typeof(IWorkItemRepository)
                    || s.ServiceType == typeof(IIterationService) || s.ServiceType == typeof(IManagedWorktreeInitializer))
                    .ShouldBeFalse();
            }).ShouldBe(expected, stderr.ToString());
            stdout.ToString().ShouldBeEmpty();
            stderr.ToString().ShouldNotBeNullOrWhiteSpace();
            if (scenario != "ids")
            {
                using var error = JsonDocument.Parse(stderr.ToString());
                error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            }
            nativeResolutions.ShouldBe(0);
            Directory.Exists(twig).ShouldBe(beforeDirectory);
            Snapshot().Keys.ShouldBe(before.Keys, ignoreOrder: true);
            foreach (var (path, bytes) in before) File.ReadAllBytes(path).ShouldBe(bytes);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => !path.StartsWith(Path.Combine(_root, ".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToDictionary(path => path, File.ReadAllBytes);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
