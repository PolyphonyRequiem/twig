using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
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
        await RunParserProcessAsync(start, root, TimeSpan.FromSeconds(10), (exitCode, output, error) =>
        {
            exitCode.ShouldBe(0);
            output.ShouldContain("--hostname");
            output.ShouldContain("--user");
            output.ShouldNotContain(Secret);
            error.ShouldNotContain(Secret);
            ParserCredentialConfigurationFiles(root).ShouldBeEmpty(
                "help must not initialize credential configuration; non-credential state directories are permitted");
        });
    }

    private async Task RunParserProcessAsync(ProcessStartInfo start, string root, TimeSpan timeout,
        Action<int, string, string> verify, Action? deleteOwnedRoot = null, Action? observeIdentity = null)
    {
        var clock = Stopwatch.StartNew();
        var phase = "startup";
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        Exception? primaryFailure = null;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("GitHub CLI parser did not start.");
            process.StandardInput.Close();
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(timeout);
            var waitStarted = clock.Elapsed;
            phase = "wait";
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var waitElapsed = clock.Elapsed - waitStarted;
                deadline.Token.ThrowIfCancellationRequested();
                if (waitElapsed >= timeout)
                    throw new OperationCanceledException("Parser process wait exceeded its deadline.", deadline.Token);
                diagnostics.WriteLine($"gh-help phase=wait elapsedMs={clock.ElapsedMilliseconds} "
                    + $"waitElapsedMs={waitElapsed.TotalMilliseconds}");
            }
            catch (OperationCanceledException timedOut)
            {
                WriteParserDiagnostics();
                phase = "reap";
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception reapFailure)
                {
                    diagnostics.WriteLine("gh-help ownedCleanup=uncertain reason=exit-or-pipe-reap-failed");
                    throw new AggregateException("Parser timed out and owned-process reaping failed.", timedOut, reapFailure);
                }
                throw;
            }
            phase = "drain";
            string output, error;
            try
            {
                output = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
                error = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException drainFailure)
            {
                phase = "drain-incomplete";
                diagnostics.WriteLine("gh-help ownedCleanup=uncertain reason=redirected-pipes-not-closed-after-root-exit");
                throw new TimeoutException(
                    "Parser root exited but redirected pipes did not close within the drain deadline. "
                    + "Owned descendant cleanup is uncertain; root exit does not prove descendant termination.",
                    drainFailure);
            }
            phase = "assert";
            verify(process.ExitCode, output, error);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                var terminalPhase = phase;
                phase = "identity-after-process-wait";
                var identityStarted = clock.ElapsedMilliseconds;
                if (observeIdentity is null)
                    WriteProcessIdentity();
                else
                    observeIdentity();
                diagnostics.WriteLine($"gh-help phase={phase} elapsedMs={clock.ElapsedMilliseconds} "
                    + $"phaseElapsedMs={clock.ElapsedMilliseconds - identityStarted} "
                    + $"processTerminalPhase={terminalPhase} processTerminalElapsedMs={identityStarted}");
                phase = terminalPhase;
                WriteParserDiagnostics();
            }
            finally
            {
                process?.Dispose();
                try
                {
                    if (deleteOwnedRoot is null)
                        Directory.Delete(root, recursive: true);
                    else
                        deleteOwnedRoot();
                }
                catch (Exception cleanupFailure) when (primaryFailure is not null)
                {
                    throw new AggregateException("Parser failed and owned-directory cleanup also failed.",
                        primaryFailure, cleanupFailure);
                }
            }
        }

        void WriteParserDiagnostics()
        {
            try
            {
                WriteProcessStatus();
                diagnostics.WriteLine(
                    $"gh-help phase={phase} elapsedMs={clock.ElapsedMilliseconds} started={process is not null} "
                    + $"exited={process?.HasExited} exitCode={(process?.HasExited == true ? (int?)process.ExitCode : null)} "
                    + $"stdoutStatus={stdout?.Status} stdoutChars={(stdout?.IsCompletedSuccessfully == true ? (int?)stdout.Result.Length : null)} "
                    + $"stderrStatus={stderr?.Status} stderrChars={(stderr?.IsCompletedSuccessfully == true ? (int?)stderr.Result.Length : null)}");
                diagnostics.WriteLine("gh-help ownedFiles=" + string.Join(",",
                    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Take(40).Select(file => Path.GetRelativePath(root, file))));
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException
                or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                diagnostics.WriteLine($"gh-help diagnosticsUnavailable={error.GetType().Name}");
            }
        }

        void WriteProcessIdentity()
        {
            if (process is null)
            {
                diagnostics.WriteLine("gh-help identityUnavailable=process-not-started");
                return;
            }
            try
            {
                var executable = process.MainModule?.FileName;
                diagnostics.WriteLine($"gh-help pid={process.Id} startUtc={process.StartTime.ToUniversalTime():O} executable={executable}");
                if (executable is null)
                    diagnostics.WriteLine("gh-help identityUnavailable=observed-image-unavailable-after-wait");
                if (executable is not null)
                {
                    using var image = File.OpenRead(executable);
                    diagnostics.WriteLine($"gh-help executableSha256={Convert.ToHexString(SHA256.HashData(image))} "
                        + $"fileVersion={FileVersionInfo.GetVersionInfo(executable).FileVersion}");
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException
                or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                diagnostics.WriteLine($"gh-help identityUnavailable={error.GetType().Name}");
            }
        }

        void WriteProcessStatus()
        {
            if (process is null)
                return;
            try
            {
                process.Refresh();
                diagnostics.WriteLine($"gh-help pid={process.Id} cpuMs={process.TotalProcessorTime.TotalMilliseconds} "
                    + $"threads={process.Threads.Count}");
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                diagnostics.WriteLine($"gh-help statusUnavailable={error.GetType().Name}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserTimeout_ReapsRealOwnedChild_AndPreservesTimeoutWhenCleanupFails(bool cleanupFails)
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = OperatingSystem.IsWindows()
            ? BoundedProcessRunner.CreateStartInfo("ping", ["-n", "30", "127.0.0.1"], [])
            : BoundedProcessRunner.CreateStartInfo("sleep", ["30"], []);
        start.WorkingDirectory = root;
        var cleanupFailure = new IOException("fixture-owned directory is unavailable");
        var clock = Stopwatch.StartNew();
        try
        {
            var failure = await Record.ExceptionAsync(() => RunParserProcessAsync(start, root,
                TimeSpan.FromMilliseconds(250), (_, _, _) => Assert.Fail("The slow child must time out."),
                cleanupFails ? () => throw cleanupFailure : null));

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
            if (cleanupFails)
            {
                var aggregate = failure.ShouldBeOfType<AggregateException>();
                aggregate.InnerExceptions.Count.ShouldBe(2);
                aggregate.InnerExceptions[0].ShouldBeAssignableTo<OperationCanceledException>();
                aggregate.InnerExceptions[1].ShouldBeSameAs(cleanupFailure);
            }
            else
            {
                failure.ShouldBeAssignableTo<OperationCanceledException>();
                Directory.Exists(root).ShouldBeFalse("reaping must precede deletion of the child's working directory");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParserTimeout_SlowIdentityObserver_CannotTurnOverDeadlineChildIntoSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = OperatingSystem.IsWindows()
            ? BoundedProcessRunner.CreateStartInfo("ping", ["-n", "3", "127.0.0.1"], [])
            : BoundedProcessRunner.CreateStartInfo("sleep", ["2"], []);
        start.WorkingDirectory = root;
        var observerCalled = false;
        var verificationCalled = false;
        var failure = await Record.ExceptionAsync(() => RunParserProcessAsync(start, root,
            TimeSpan.FromMilliseconds(250), (_, _, _) => verificationCalled = true,
            observeIdentity: () =>
            {
                observerCalled = true;
                Thread.Sleep(TimeSpan.FromSeconds(4));
            }));

        failure.ShouldBeAssignableTo<OperationCanceledException>();
        observerCalled.ShouldBeTrue();
        verificationCalled.ShouldBeFalse("a slow observer must not hide expiration behind an already-exited child");
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public async Task ParserCleanupFailure_AfterRealChildSuccess_IsNotSuppressed()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-gh-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = OperatingSystem.IsWindows()
            ? BoundedProcessRunner.CreateStartInfo("cmd", ["/d", "/c", "exit", "0"], [])
            : BoundedProcessRunner.CreateStartInfo("/bin/sh", ["-c", "exit 0"], []);
        start.WorkingDirectory = root;
        var cleanupFailure = new IOException("fixture-owned directory is unavailable");
        try
        {
            var failure = await Record.ExceptionAsync(() => RunParserProcessAsync(start, root,
                TimeSpan.FromSeconds(10), (code, _, _) => code.ShouldBe(0),
                () => throw cleanupFailure));

            failure.ShouldBeSameAs(cleanupFailure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
