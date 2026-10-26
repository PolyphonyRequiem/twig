using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;
using Xunit.Abstractions;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GhCliTokenSourceTests(ITestOutputHelper diagnostics)
{
    private const string Secret = "gho_SECRETtoken123";

    [Fact]
    public async Task ProductionArguments_AreAcceptedByInstalledGhHelp_WithoutCredentialLookup()
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 0, Secret));
        (await new GhCliTokenSource(runner, "github.com", "twig-parser-fixture")
            .GetTokenAsync(default)).ShouldBe(Secret);
        var call = runner.Calls.Single();
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        var phase = "startup";
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        try
        {
            var start = BoundedProcessRunner.CreateStartInfo(call.FileName,
                [.. call.Arguments, "--help"], runner.ExcludedEnvironment);
            foreach (var (key, directory) in new[]
            {
                ("HOME", "home"), ("USERPROFILE", "home"), ("TWIG_USER_HOME", "twig"),
                ("GH_CONFIG_DIR", "gh-config"), ("XDG_CONFIG_HOME", "xdg-config"),
                ("XDG_CACHE_HOME", "xdg-cache"), ("XDG_STATE_HOME", "xdg-state"),
                ("XDG_DATA_HOME", "xdg-data"), ("APPDATA", "appdata"), ("LOCALAPPDATA", "localappdata"),
            })
            {
                var path = Path.Combine(root, directory);
                Directory.CreateDirectory(path);
                start.Environment[key] = path;
            }
            start.WorkingDirectory = start.Environment["HOME"]!;
            start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            start.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
            start.Environment["GH_PROMPT_DISABLED"] = "1";
            start.Environment["GH_TELEMETRY"] = "disabled";
            start.Environment["DO_NOT_TRACK"] = "1";
            start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
            process = Process.Start(start)
                ?? throw new InvalidOperationException("GitHub CLI parser did not start.");
            process.StandardInput.Close();
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            phase = "wait";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                WriteParserDiagnostics();
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
            phase = "drain";
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            phase = "assert";
            process.ExitCode.ShouldBe(0);
            output.ShouldContain("--hostname");
            output.ShouldContain("--user");
            output.ShouldNotContain(Secret);
            error.ShouldNotContain(Secret);
            ParserCredentialConfigurationFiles(root).ShouldBeEmpty(
                "help must not initialize credential configuration; non-credential state directories are permitted");
        }
        finally
        {
            WriteParserDiagnostics();
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }

        void WriteParserDiagnostics()
        {
            diagnostics.WriteLine(
                $"gh-help phase={phase} elapsedMs={clock.ElapsedMilliseconds} started={process is not null} "
                + $"exited={process?.HasExited} exitCode={(process?.HasExited == true ? (int?)process.ExitCode : null)} "
                + $"stdoutStatus={stdout?.Status} stdoutChars={(stdout?.IsCompletedSuccessfully == true ? (int?)stdout.Result.Length : null)} "
                + $"stderrStatus={stderr?.Status} stderrChars={(stderr?.IsCompletedSuccessfully == true ? (int?)stderr.Result.Length : null)}");
            diagnostics.WriteLine("gh-help ownedFiles=" + string.Join(",",
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Take(40).Select(file => Path.GetRelativePath(root, file))));
        }
    }

    [Fact]
    public void ParserIsolation_AllowsNonCredentialState()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "home", ".local", "state", "gh");
        Directory.CreateDirectory(state);
        try
        {
            File.WriteAllText(Path.Combine(state, "device-id"), "fixture-device-id");
            ParserCredentialConfigurationFiles(root).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("gh-config", "", "hosts.yml")]
    [InlineData("gh-config", "", "config.yml")]
    [InlineData("xdg-config", "gh", "hosts.yml")]
    [InlineData("appdata", "GitHub CLI", "config.yml")]
    [InlineData("home", ".config", "hosts.yml")]
    [InlineData("xdg-state", "gh", "hosts.yml")]
    public void ParserIsolation_DetectsCredentialConfiguration(string directory, string child, string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, directory, child);
        Directory.CreateDirectory(path);
        try
        {
            var file = Path.Combine(path, name);
            File.WriteAllText(file, "fixture-only");
            ParserCredentialConfigurationFiles(root).ShouldBe([file]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<string> ParserCredentialConfigurationFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(file =>
        {
            var name = Path.GetFileName(file);
            var directory = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[0];
            return directory is "gh-config" or "xdg-config" or "appdata"
                || string.Equals(name, "hosts.yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "config.yml", StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public async Task Success_RunsGhAuthTokenWithArgumentArrayAndBounds_AndCachesInMemory()
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 0, Secret + "\n"));
        var source = new GhCliTokenSource(runner, "github.com", "selected-account");

        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);

        runner.Calls.Count.ShouldBe(1);
        var call = runner.Calls[0];
        call.FileName.ShouldBe("gh");
        call.Arguments.ShouldBe(["auth", "token", "--hostname", "github.com", "--user", "selected-account"]);
        runner.ExcludedEnvironment.ShouldBe(["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]);
        call.Timeout.ShouldBe(GhCliTokenSource.Timeout);
        call.Timeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        call.MaxOutputChars.ShouldBe(GhCliTokenSource.MaxOutputChars);
    }

    public static TheoryData<string, int, string, string> Failures => new()
    {
        { nameof(BoundedProcessOutcome.NotStarted), -1, "", "not found" },
        { nameof(BoundedProcessOutcome.TimedOut), -1, "", "did not finish in time" },
        { nameof(BoundedProcessOutcome.OutputLimitExceeded), -1, "", "unexpected output" },
        { nameof(BoundedProcessOutcome.Exited), 1, Secret, "exited with code 1" },
        { nameof(BoundedProcessOutcome.Exited), 0, "", "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, "   \n", "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, Secret + "\n" + Secret, "no usable token" },
        { nameof(BoundedProcessOutcome.Exited), 0, "not logged in " + Secret, "no usable token" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failure_FailsClosedWithActionableMessage_AndNeverDisclosesOutput(
        string outcomeName, int exitCode, string stdout, string expected)
    {
        var outcome = Enum.Parse<BoundedProcessOutcome>(outcomeName);
        var runner = new FakeRunner(new(outcome, exitCode, stdout));
        var source = new GhCliTokenSource(runner, "ghe.example.com", "selected-account");

        var ex = await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            () => source.GetTokenAsync(CancellationToken.None).AsTask());

        ex.Message.ShouldContain(expected);
        ex.Message.ShouldContain("gh auth login --hostname ghe.example.com");
        ex.Message.ShouldNotContain(Secret);
        ex.ToString().ShouldNotContain(Secret);
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Failure_IsNotCached_SoALaterLoginIsPickedUp()
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 1, ""));
        var source = new GhCliTokenSource(runner, "github.com", "selected-account");
        await Should.ThrowAsync<GitHubCredentialUnavailableException>(() => source.GetTokenAsync(CancellationToken.None).AsTask());

        runner.Next = new(BoundedProcessOutcome.Exited, 0, Secret);
        (await source.GetTokenAsync(CancellationToken.None)).ShouldBe(Secret);
        runner.Calls.Count.ShouldBe(2);
    }

    internal sealed class FakeRunner(BoundedProcessResult result) : IBoundedProcessRunner
    {
        public IReadOnlyList<string>? ExcludedEnvironment { get; private set; }
        public BoundedProcessResult Next { get; set; } = result;
        public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, int MaxOutputChars)> Calls { get; } = [];

        public Task<BoundedProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, int maxOutputChars, CancellationToken ct,
            IReadOnlyList<string>? excludedEnvironmentVariables = null)
        {
            ExcludedEnvironment = excludedEnvironmentVariables;
            Calls.Add((fileName, arguments.ToArray(), timeout, maxOutputChars));
            return Task.FromResult(Next);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" user")]
    [InlineData("bad\nuser")]
    [InlineData("--account")]
    public async Task MissingOrInvalidAccount_NeverStartsCredentialProcess(string? account)
    {
        var runner = new FakeRunner(new(BoundedProcessOutcome.Exited, 0, Secret));
        await Should.ThrowAsync<GitHubCredentialUnavailableException>(
            () => new GhCliTokenSource(runner, "github.com", account).GetTokenAsync(default).AsTask());
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void ChildEnvironment_ExcludesCredentialOverrides()
    {
        var start = BoundedProcessRunner.CreateStartInfo("gh", ["auth", "token", "--user", "selected-account"],
            null);
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" })
            start.Environment[key] = "fixture-only-override";
        start.Environment["TWIG_FIXTURE_KEEP"] = "keep";
        BoundedProcessRunner.ExcludeEnvironmentVariables(start,
            ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]);
        start.Environment.ContainsKey("GH_TOKEN").ShouldBeFalse();
        start.Environment.ContainsKey("GITHUB_TOKEN").ShouldBeFalse();
        start.Environment.ContainsKey("GH_ENTERPRISE_TOKEN").ShouldBeFalse();
        start.Environment.ContainsKey("GITHUB_ENTERPRISE_TOKEN").ShouldBeFalse();
        start.Environment["TWIG_FIXTURE_KEEP"].ShouldBe("keep");
        start.ArgumentList.ShouldContain("selected-account");
    }
}
