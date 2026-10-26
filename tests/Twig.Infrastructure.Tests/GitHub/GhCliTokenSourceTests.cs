using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GhCliTokenSourceTests
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
        try
        {
            var start = BoundedProcessRunner.CreateStartInfo(call.FileName,
                [.. call.Arguments, "--help"], runner.ExcludedEnvironment);
            foreach (var key in new[] { "GH_CONFIG_DIR", "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "TWIG_USER_HOME" })
                start.Environment[key] = root;
            start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            start.Environment["GH_PROMPT_DISABLED"] = "1";
            start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("GitHub CLI parser did not start.");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            process.ExitCode.ShouldBe(0, error);
            output.ShouldContain("--hostname");
            output.ShouldContain("--user");
            output.ShouldNotContain(Secret);
            Directory.GetFileSystemEntries(root).ShouldBeEmpty("help must not initialize credential configuration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
