using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

/// <summary>
/// Ticket 0018 — startup ordering: no side effects above the fast-exit block.
///
/// <para>
/// <c>twig --help</c> took 6.5 seconds on the first invocation after an upgrade because
/// <c>SelfUpdater.CleanupOldBinary()</c> and <c>CompanionStartup.RunFirstRunCheck()</c> ran
/// unconditionally at the top of <c>Program.cs</c>, above the fast-exit block that handles
/// <c>--version</c>, <c>-h</c>/<c>--help</c>/<c>help</c>, the no-args smart landing and the
/// unknown-command interception. <c>RunFirstRunCheck</c> performs a blocking GitHub release
/// download with a 60-second budget, so every fast-exit path paid for a network install it
/// could never need.
/// </para>
///
/// <para>
/// The guard is deliberately <b>behavioural on source ordering, not wall-clock</b> (0011 §2:
/// a timing assertion in CI would be flaky). It asserts that neither side effect — and no
/// <c>HttpClient</c> construction — appears above the fast-exit returns, while both still run
/// for real commands.
/// </para>
/// </summary>
public sealed class StartupOrderingTests
{
    private static string ReadProgramSource()
    {
        var repoRoot = BuildFixture.FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "Twig", "Program.cs");
        File.Exists(path).ShouldBeTrue($"Program.cs not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The top-level statement section of Program.cs — everything before the first
    /// declaration that follows <c>app.Run(args)</c>. Guards against matching the
    /// helper class bodies further down the file.
    /// </summary>
    private static string ReadTopLevelStatements()
    {
        var source = ReadProgramSource();
        var end = source.IndexOf("static (string? GitProject, string? Repository) DetectGitRemote", StringComparison.Ordinal);
        end.ShouldBeGreaterThan(0, "DetectGitRemote marker missing — update this fixture.");
        return source[..end];
    }

    /// <summary>
    /// Index of the LAST fast-exit early return in the top-level statements — the
    /// unknown-command interception. Any startup side effect must appear after it.
    /// </summary>
    private static int FastExitEndIndex(string top)
    {
        var marker = "GroupedHelp.ShowUnknown(args[0]);";
        var idx = top.IndexOf(marker, StringComparison.Ordinal);
        idx.ShouldBeGreaterThan(0, "Unknown-command fast-exit marker missing — update this fixture.");
        return idx;
    }

    [Fact]
    public void FastExitBlock_IsPrecededBy_TheFourFastExitPaths()
    {
        // Precondition: the fast-exit block still handles all four paths. If a future
        // refactor removes them this test must not silently degrade into a no-op.
        var top = ReadTopLevelStatements();

        top.ShouldContain("args[0] == \"--version\"");
        top.ShouldContain("\"-h\" or \"--help\" or \"help\"");
        top.ShouldContain("args.Length == 0");
        top.ShouldContain("GroupedHelp.IsKnownCommand(args)");
    }

    [Theory]
    [InlineData("SelfUpdater.CleanupOldBinary()")]
    [InlineData("CompanionStartup.RunFirstRunCheck()")]
    public void StartupSideEffect_RunsBelowTheFastExitBlock(string call)
    {
        var top = ReadTopLevelStatements();
        var fastExitEnd = FastExitEndIndex(top);

        var callIndex = top.IndexOf(call, StringComparison.Ordinal);

        callIndex.ShouldBeGreaterThan(
            -1,
            $"'{call}' must still run for real commands — this is a reordering, not a removal.");

        callIndex.ShouldBeGreaterThan(
            fastExitEnd,
            $"TICKET-0018: '{call}' runs ABOVE the fast-exit block, so --version/--help/no-args/"
            + "unknown-command pay for a startup side effect they can never need "
            + "(RunFirstRunCheck is a blocking GitHub download with a 60s budget).");
    }

    [Fact]
    public void FastExitPaths_ConstructNoHttpClient()
    {
        // Acceptance from the ticket: zero network calls on the fast-exit paths.
        var top = ReadTopLevelStatements();
        var aboveFastExitEnd = top[..FastExitEndIndex(top)];

        // No HttpClient may be constructed above the fast-exit block.
        aboveFastExitEnd.ShouldNotContain("CreateHttpClient");
        aboveFastExitEnd.ShouldNotContain("new HttpClient");
    }
}
