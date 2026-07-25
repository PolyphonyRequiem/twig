using NSubstitute;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#252 — <c>twig sync</c>'s <c>flush.*</c>
/// counters reported <c>0</c> for two opposite conditions:
/// <list type="bullet">
///   <item>"nothing was staged" (benign — e.g. <c>twig patch</c> already wrote through), and</item>
///   <item>"something was staged but never reached ADO" (data loss — issue #251).</item>
/// </list>
/// The fix makes absence the signal for the benign case: a counter is emitted only when
/// its class actually had something staged. A <em>present</em> zero, and the explicit
/// <c>notesDropped</c>/<c>fieldChangesDropped</c> keys, now name the lossy case.
/// </summary>
public sealed class SyncCommandFlushCounterAmbiguityTests : RefreshCommandTestBase
{
    private readonly IPendingChangeFlusher _flusher = Substitute.For<IPendingChangeFlusher>();

    private SyncCommand CreateSyncCommand(TextWriter? stderr = null) =>
        new(_flusher, CreateRefreshCommand(stderr), _formatterFactory, stderr);

    private static async Task<string> CaptureStdoutAsync(Func<Task> action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try { await action(); }
        finally { Console.SetOut(originalOut); }
        return stdout.ToString();
    }

    private void Flush(FlushResult result) =>
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);

    // ═══════════════════════════════════════════════════════════════
    //  Benign case: nothing staged → counters omitted entirely
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Json_NothingStaged_OmitsPushedCountersEntirely()
    {
        // The #252 repro: `twig patch` wrote through, so sync legitimately has nothing
        // to flush. Previously this rendered `"fieldChangesPushed": 0, "notesPushed": 0`,
        // indistinguishable from a dropped write.
        Flush(new FlushResult(0, 0, 0, []));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"flush\"");
        output.ShouldNotContain("fieldChangesPushed");
        output.ShouldNotContain("notesPushed");
        output.ShouldNotContain("fieldChangesDropped");
        output.ShouldNotContain("notesDropped");
        output.ShouldContain("\"failed\": 0");
    }

    [Fact]
    public async Task Json_NothingStaged_DoesNotWarnOnStderr()
    {
        Flush(new FlushResult(0, 0, 0, []));
        var stderr = new StringWriter();

        await CaptureStdoutAsync(() => CreateSyncCommand(stderr).ExecuteAsync(outputFormat: "json"));

        stderr.ToString().ShouldNotContain("staged but not pushed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Lossy case: staged but not pushed → present, named, and loud
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Json_NoteStagedButNotPushed_EmitsZeroPushedAndNotesDropped()
    {
        // The #251 shape: one note was staged, none reached ADO, no failure recorded.
        Flush(new FlushResult(0, 0, 0, [], NotesStaged: 1));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"notesStaged\": 1");
        output.ShouldContain("\"notesPushed\": 0");
        output.ShouldContain("\"notesDropped\": 1");
        // Field-change counters stay absent — nothing of that class was staged.
        output.ShouldNotContain("fieldChangesPushed");
    }

    [Fact]
    public async Task Json_NoteStagedButNotPushed_WarnsOnStderr()
    {
        Flush(new FlushResult(0, 0, 0, [], NotesStaged: 1));
        var stderr = new StringWriter();

        await CaptureStdoutAsync(() => CreateSyncCommand(stderr).ExecuteAsync(outputFormat: "json"));

        var err = stderr.ToString();
        err.ShouldContain("1 note(s)");
        err.ShouldContain("staged but not pushed");
    }

    [Fact]
    public async Task Json_FieldChangeStagedButNotPushed_EmitsFieldChangesDropped()
    {
        Flush(new FlushResult(0, 0, 0, [], FieldChangesStaged: 3));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"fieldChangesStaged\": 3");
        output.ShouldContain("\"fieldChangesPushed\": 0");
        output.ShouldContain("\"fieldChangesDropped\": 3");
        output.ShouldNotContain("notesPushed");
    }

    [Fact]
    public async Task Json_PartialPush_ReportsOnlyTheShortfallAsDropped()
    {
        Flush(new FlushResult(1, 2, 1, [], FieldChangesStaged: 5, NotesStaged: 3));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"fieldChangesDropped\": 3");
        output.ShouldContain("\"notesDropped\": 2");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Fully-pushed case: counters present, no drop keys
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Json_EverythingPushed_EmitsCountersWithoutDropKeys()
    {
        Flush(new FlushResult(2, 4, 1, [], FieldChangesStaged: 4, NotesStaged: 1));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"fieldChangesPushed\": 4");
        output.ShouldContain("\"notesPushed\": 1");
        output.ShouldNotContain("Dropped");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Human + minimal output must not say "nothing to flush" on a drop
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Human_StagedButNotPushed_DoesNotClaimNothingToFlush()
    {
        Flush(new FlushResult(0, 0, 0, [], NotesStaged: 1));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync());

        output.ShouldNotContain("nothing to flush");
    }

    [Fact]
    public async Task Human_NothingStaged_StillSaysNothingToFlush()
    {
        Flush(new FlushResult(0, 0, 0, []));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync());

        output.ShouldContain("nothing to flush");
    }

    [Fact]
    public async Task Minimal_StagedButNotPushed_ReportsDropCount()
    {
        Flush(new FlushResult(0, 0, 0, [], NotesStaged: 2));

        var output = await CaptureStdoutAsync(() => CreateSyncCommand().ExecuteAsync(outputFormat: "minimal"));

        output.ShouldContain("dropped: 2");
        output.ShouldNotContain("nothing to flush");
    }

    // ═══════════════════════════════════════════════════════════════
    //  pull-only must not fabricate a drop warning
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PullOnly_NoFlushPhase_EmitsNoCountersAndNoWarning()
    {
        var stderr = new StringWriter();

        var output = await CaptureStdoutAsync(() =>
            CreateSyncCommand(stderr).ExecuteAsync(outputFormat: "json", pullOnly: true));

        output.ShouldNotContain("notesPushed");
        output.ShouldNotContain("fieldChangesPushed");
        stderr.ToString().ShouldNotContain("staged but not pushed");
    }
}
