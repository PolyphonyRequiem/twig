using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Ticket 0019 — the entrypoint guard, end to end.
///
/// <para>
/// <see cref="OutputFormatsAcceptListTests"/> pins the accept-list and the validator, but every
/// one of its cases calls <c>OutputFormatArgumentValidator.Validate</c> <b>directly</b>. The
/// validator has exactly one production call site — the guard block in <c>Program.cs</c> — so
/// deleting that block left the whole unit suite green while <c>-o jsno</c> silently returned to
/// emitting human output with exit 0. That is the precise bug this ticket exists to kill, and it
/// was the one thing with no committed guard.
/// </para>
///
/// <para>
/// These tests close that gap by running the built binary. They are the tests that fail if the
/// entrypoint validation is reverted.
/// </para>
///
/// <para>
/// Uses <see cref="BuildFixture"/> (shared with <c>AotSmokeTests</c>) so the binary is built once
/// before the class runs, regardless of xUnit execution order.
/// </para>
///
/// <para>
/// Deliberately NOT marked <c>[Trait("Category", "Interactive")]</c>. <c>AotSmokeTests</c> carries
/// that trait and the default filter excludes it, so copying the trait here would have left these
/// tests never running in a normal <c>dotnet test</c> — a guard that guards nothing, which is the
/// very failure this file exists to correct.
/// </para>
/// </summary>
public sealed class OutputFormatEntrypointTests : IClassFixture<BuildFixture>
{
    private readonly BuildFixture _build;

    public OutputFormatEntrypointTests(BuildFixture build)
    {
        _build = build;
    }

    /// <summary>
    /// Runs the built <c>twig</c> assembly via <c>dotnet</c>, so no AOT publish is required.
    /// </summary>
    private (string Stdout, string Stderr, int ExitCode, bool Exited) RunTwig(string arguments)
    {
        _build.BuildSucceeded.ShouldBeTrue(
            $"Build failed or timed out; cannot exercise the entrypoint.\nstdout:\n{_build.BuildStdout}\nstderr:\n{_build.BuildStderr}");

        var dll = Path.Combine(_build.RepoRoot, "src", "Twig", "bin", "Debug", "net11.0", "twig.dll");
        File.Exists(dll).ShouldBeTrue($"Built twig.dll not found at {dll} — update this fixture.");

        return BuildFixture.RunProcess("dotnet", $"\"{dll}\" {arguments}", timeoutMinutes: 2);
    }

    [Theory]
    [InlineData("jsno")]      // transposition typo
    [InlineData("json5")]     // plausible-but-unsupported
    [InlineData("JSON5")]     // and case-insensitively
    [InlineData("yaml")]      // never supported
    public void UnknownFormat_ExitsNonZero_AtTheEntrypoint(string format)
    {
        var (stdout, stderr, exitCode, exited) = RunTwig($"show -o {format}");

        exited.ShouldBeTrue($"twig show -o {format} timed out");

        exitCode.ShouldBe(
            OutputFormatArgumentValidator.UsageExitCode,
            $"TICKET-0019: `-o {format}` must be rejected at the entrypoint with a usage exit code. "
            + $"Got exit {exitCode}. If this is 0, the entrypoint guard in Program.cs is gone and "
            + $"malformed output is reaching stdout — the exact failure this ticket closed.\n"
            + $"stdout:\n{stdout}\nstderr:\n{stderr}");

        stderr.ShouldContain(
            format,
            Case.Insensitive,
            "The error must name the offending value so the user can see their typo.");
    }

    [Fact]
    public void UnknownFormat_ErrorNamesEveryValidFormat_OnStderr()
    {
        var (stdout, stderr, _, exited) = RunTwig("show -o jsno");

        exited.ShouldBeTrue("twig show -o jsno timed out");

        foreach (var accepted in OutputFormats.Accepted)
        {
            stderr.ShouldContain(
                accepted,
                Case.Insensitive,
                $"The usage error must name '{accepted}' so the user can recover without reading docs.\n"
                + $"stderr:\n{stderr}");
        }

        stdout.ShouldBeEmpty("A usage error must not emit anything on stdout — stdout may be piped into jq.");
    }

    [Fact]
    public void AcceptedFormat_IsNotRejectedByTheEntrypointGuard()
    {
        // Positive control, deliberately in-process rather than via the binary.
        //
        // Without a positive control the theory above would still pass if the guard rejected
        // EVERYTHING. But running `show -o json` through the binary reaches a REAL command, which
        // pays for the 0018 startup side effects — a blocking GitHub companion download — and that
        // blew the 300 s test-run budget and aborted the host. The negative cases never reach it
        // because the guard returns first, which is precisely the point of the guard.
        //
        // So the accepted-format direction is asserted against the validator directly. The
        // binary-level tests above are what pin the guard's EXISTENCE at the entrypoint; this pins
        // that the guard is not indiscriminate.
        foreach (var accepted in OutputFormats.Accepted)
        {
            OutputFormatArgumentValidator.Validate(["show", "-o", accepted])
                .ShouldBeNull($"'{accepted}' is on the accept-list and must pass the gate.");
        }
    }
}
