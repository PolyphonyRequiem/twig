using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>Exercises the real process runner against local executables only — never gh.</summary>
public sealed class BoundedProcessRunnerTests
{
    private static readonly string Dotnet = Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p) == "dotnet"
        ? p
        : "dotnet";

    [Fact]
    public async Task MissingExecutable_IsNotStarted()
    {
        var result = await new BoundedProcessRunner().RunAsync(
            "twig-no-such-executable-" + Guid.NewGuid().ToString("N"), [], TimeSpan.FromSeconds(5), 100, CancellationToken.None);

        result.Outcome.ShouldBe(BoundedProcessOutcome.NotStarted);
        result.StandardOutput.ShouldBeEmpty();
    }

    [Fact]
    public async Task Exit_ReturnsCodeAndOutput()
    {
        var result = await new BoundedProcessRunner().RunAsync(
            Dotnet, ["--version"], TimeSpan.FromSeconds(20), 4096, CancellationToken.None);

        result.Outcome.ShouldBe(BoundedProcessOutcome.Exited);
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.Trim().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task OutputOverLimit_IsRefusedAndDiscarded()
    {
        var result = await new BoundedProcessRunner().RunAsync(
            Dotnet, ["--version"], TimeSpan.FromSeconds(20), 2, CancellationToken.None);

        result.Outcome.ShouldBe(BoundedProcessOutcome.OutputLimitExceeded);
        result.StandardOutput.ShouldBeEmpty();
    }

    [Fact]
    public async Task SlowProcess_TimesOutPromptly()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });
        var clock = Stopwatch.StartNew();

        var result = await new BoundedProcessRunner().RunAsync(
            file, args, TimeSpan.FromMilliseconds(500), 1_000_000, CancellationToken.None);

        result.Outcome.ShouldBe(BoundedProcessOutcome.TimedOut);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }
}
