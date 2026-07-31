using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Regression tests for twig#311 / ADO #39.
///
/// The defect: <see cref="BuildFixture.RunProcess"/> timed out only WaitForExit, then
/// blocked forever on an untimed <c>stdoutTask.GetAwaiter().GetResult()</c>. A
/// GRANDCHILD process that inherited the redirected stdout handle kept the pipe open
/// after the direct child exited, so ReadToEnd never saw EOF. Because this ran in a
/// fixture constructor, the whole Cli suite stopped dispatching and died at the 300 s
/// vstest session timeout printing a false-green summary.
///
/// These tests reproduce that shape directly: a shell that spawns a background process
/// holding stdout open, then exits immediately. Against the old implementation the
/// first test hangs forever (and would abort the run); against the fixed one it returns
/// within the bounded grace period.
/// </summary>
public class BuildFixtureRunProcessTests
{
    /// <summary>
    /// Upper bound for a call that MUST return. Generous versus the 30 s internal
    /// grace period, but far below the 300 s session timeout, so a regression fails
    /// this test honestly instead of aborting the whole run.
    /// </summary>
    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(120);

    [Fact]
    public void RunProcess_WhenGrandchildHoldsStdoutOpen_StillReturns()
    {
        // A grandchild inherits stdout and holds it open well past the parent's exit.
        // The direct child exits immediately, so WaitForExit returns fast — exactly the
        // #311 shape, where the surviving handle is what blocks the reader.
        var script = "sleep 3600 & echo parent-done";

        var sw = Stopwatch.StartNew();
        var (stdout, _, _, exited) = RunGuarded("/bin/sh", $"-c \"{script}\"");
        sw.Stop();

        exited.ShouldBeTrue("the direct child exits immediately; only the pipe lingers");

        // The contract is that it RETURNS, not that output survives. Abandoning a
        // stuck reader (empty stdout) is an acceptable, documented outcome.
        sw.Elapsed.ShouldBeLessThan(
            MustReturnWithin,
            "RunProcess must never block unboundedly on a pipe held by a grandchild " +
            "— that is twig#311. Returning truncated output is the intended tradeoff.");

        stdout.ShouldNotBeNull();
    }

    [Fact]
    public void RunProcess_OnNormalProcess_CapturesOutputAndExitCode()
    {
        // Guard against "fixing" the hang by simply never reading the pipes.
        var (stdout, _, exitCode, exited) = RunGuarded("/bin/sh", "-c \"echo hello-twig\"");

        exited.ShouldBeTrue();
        exitCode.ShouldBe(0);
        stdout.ShouldContain("hello-twig");
    }

    [Fact]
    public void RunProcess_OnNonZeroExit_ReportsExitCode()
    {
        var (_, _, exitCode, exited) = RunGuarded("/bin/sh", "-c \"exit 3\"");

        exited.ShouldBeTrue();
        exitCode.ShouldBe(3);
    }

    /// <summary>
    /// Invokes RunProcess on a worker thread with a hard cap, so a REGRESSION fails
    /// this test rather than hanging the fixture and aborting the entire run at the
    /// session timeout (the original #311 failure mode, which reports false-green).
    /// </summary>
    private static (string Stdout, string Stderr, int ExitCode, bool Exited) RunGuarded(
        string fileName, string arguments)
    {
        (string, string, int, bool) result = default;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = BuildFixture.RunProcess(fileName, arguments, timeoutMinutes: 1);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        worker.Start();

        if (!worker.Join(MustReturnWithin))
        {
            throw new Xunit.Sdk.XunitException(
                $"RunProcess did not return within {MustReturnWithin.TotalSeconds:N0}s — " +
                "this is the twig#311 unbounded-pipe-read hang. The thread is abandoned " +
                "(background) so the suite can still report rather than abort.");
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"RunProcess threw: {failure}");
        }

        return result;
    }
}
