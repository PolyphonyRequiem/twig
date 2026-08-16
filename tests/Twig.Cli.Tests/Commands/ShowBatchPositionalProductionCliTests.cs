using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#501 — <c>twig show-batch &lt;ids&gt;</c> accepts a bare, comma-separated id list, exercised
/// through the REAL production CLI.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>These MUST run the built binary, not the command classes.</b> The defect lived entirely
/// at the CLI parse layer: <c>twig show-batch 154,140,390</c> answered
/// <c>Argument '154,140,390' is not recognized.</c> while every unit test over
/// <see cref="Twig.Commands.ShowCommand"/> was green, because those tests call
/// <c>ExecuteBatchAsync</c> directly and skip the source-generated argument binder. A test that
/// constructs the command class cannot observe this defect in either direction.
/// </para>
/// <para>
/// Modelled on <see cref="ProcessOverrideProductionCliTests"/> (AB#216). No ADO stub is needed:
/// these arms assert on the PARSE layer, and with no workspace present the workspace refusal is
/// itself proof the parser accepted the argument — anything downstream of
/// <c>is not recognized</c> means the argument surface bound the value, which IS the property
/// under test. Nothing here touches a live board; <c>show-batch</c> is read-only and cache-only.
/// </para>
/// <para>
/// 🔴 <b>What this file CANNOT prove, and where that proof lives instead.</b> Because these arms
/// run with no populated cache, the workspace refusal fires BEFORE the resolved id list is ever
/// read — so two different resolutions of the same invocation produce byte-identical output.
/// Mutation proved it: dropping the positional's value, and inverting the named/positional
/// precedence, both SURVIVED every arm here. The value-resolution rule is therefore pinned
/// directly on <see cref="TwigCommands.ResolveBatch"/> in
/// <see cref="ShowBatchResolutionTests"/>. Do not "strengthen" the arms below to cover it; they
/// structurally cannot.
/// </para>
/// </remarks>
public sealed class ShowBatchPositionalProductionCliTests : IDisposable
{
    private readonly string _scratchRoot =
        Path.Combine(Path.GetTempPath(), $"twig-showbatch-positional-{Guid.NewGuid():N}");

    public ShowBatchPositionalProductionCliTests() => Directory.CreateDirectory(_scratchRoot);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_scratchRoot))
            Directory.Delete(_scratchRoot, recursive: true);
    }

    /// <summary>
    /// The card's reported symptom, at every arity a user plausibly types.
    /// </summary>
    /// <remarks>
    /// 🔴 The boundary sweep is the point, not decoration. AB#398's card generalised from a
    /// single input arity and reported "the second word is rejected" when in truth the FIRST
    /// was, which sent an approved fix at the wrong shape. Measured here pre-fix, every one of
    /// these was rejected and the error named the FIRST token.
    /// </remarks>
    [Theory]
    [InlineData("154")]
    [InlineData("154,140")]
    [InlineData("154,140,390")]
    public async Task BareIdList_IsAcceptedByTheParser(string ids)
    {
        var (_, stdout, stderr) = await RunTwigAsync("show-batch", ids);

        (stdout + stderr).ShouldNotContain("is not recognized", Case.Insensitive,
            customMessage: $"'twig show-batch {ids}' is the spelling AB#501 made legal.");
    }

    /// <summary>
    /// Acceptance: the NAMED spelling is unchanged. Positionals are ADDED, never substituted.
    /// </summary>
    /// <remarks>
    /// 🔴 This arm exists because AB#398 regressed <c>init --org/--project</c> and
    /// <c>edit --field</c> by converting named options into positionals rather than adding
    /// alongside them. Same mistake, one edit away, on a command in the same family.
    /// </remarks>
    [Theory]
    [InlineData("--batch", "154,140,390")]
    [InlineData("--batch", "42")]
    public async Task NamedSpelling_StillParses(params string[] args)
    {
        var (_, stdout, stderr) = await RunTwigAsync(["show-batch", .. args]);

        (stdout + stderr).ShouldNotContain("is not recognized", Case.Insensitive,
            customMessage: "--batch was never broken and must stay that way.");
    }

    /// <summary>
    /// Supplying BOTH spellings is accepted rather than refused as a conflict.
    /// </summary>
    /// <remarks>
    /// Which one WINS is asserted in <see cref="ShowBatchResolutionTests"/>; this arm only
    /// pins that the combination parses, which is all the CLI layer can observe.
    /// </remarks>
    [Fact]
    public async Task BothSpellingsTogether_AreAcceptedByTheParser()
    {
        var (_, stdout, stderr) = await RunTwigAsync("show-batch", "999", "--batch", "154,140");

        (stdout + stderr).ShouldNotContain("is not recognized", Case.Insensitive);
    }

    /// <summary>
    /// AB#352's lesson: a usage error for a request that SUCCEEDED is a false RED.
    /// </summary>
    [Fact]
    public async Task HelpRequest_ExitsZero_AndDocumentsThePositional()
    {
        var (exitCode, stdout, stderr) = await RunTwigAsync("show-batch", "--help");

        exitCode.ShouldBe(0, "a help request succeeded; failing it would be a false RED.");

        var combined = stdout + stderr;
        combined.ShouldContain("Arguments:",
            customMessage: "the positional slot must be documented, or help and parser drift apart "
                + "in the direction tools/positional-drift.py exists to catch.");
        combined.ShouldContain("--batch",
            customMessage: "the named spelling must stay documented — it is what the examples use.");
    }

    /// <summary>
    /// The positional spelling is DOCUMENTED in the EXAMPLES block, not merely accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Added after mutation: deleting the positional example from
    /// <see cref="Twig.CommandExamples"/> left the entire suite green. The generated parser
    /// emits its own <c>Arguments:</c> block from the SLOT, so the arm above stays green with
    /// no example present, and help/parser drift in the direction
    /// <c>tools/positional-drift.py</c> exists to catch — silently.
    /// </para>
    /// <para>
    /// 🔴 The assertion is scoped to the text AFTER <c>Examples:</c> deliberately. A whole-output
    /// <c>ShouldContain("twig show-batch 1234,5678,9012")</c> is a TAUTOLOGY here: that exact
    /// string also appears in the positional's own XML doc summary, which the parser prints in
    /// the <c>Arguments:</c> block. It passed against the deleted example and proved nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Help_DocumentsTheBareIdListSpelling_InTheExamplesBlock()
    {
        var (_, stdout, stderr) = await RunTwigAsync("show-batch", "--help");
        var combined = stdout + stderr;

        var index = combined.IndexOf("Examples:", StringComparison.Ordinal);
        index.ShouldBeGreaterThan(-1, "show-batch must ship examples at all.");

        combined[index..].ShouldContain("twig show-batch 1234,5678,9012 ",
            customMessage: "a spelling the parser accepts but no EXAMPLE documents is a feature "
                + "nobody can discover. The trailing space excludes the '--batch 1234,5678,9012' "
                + "examples, which would otherwise satisfy this assertion.");
    }

    /// <summary>
    /// Supplying NO ids at all is a usage error, not a silent success.
    /// </summary>
    /// <remarks>
    /// 🔴 Making <c>batch</c> optional, so the positional may be omitted, retires the generated
    /// parser's own <c>[Required]</c> check — which is what previously printed help and exited
    /// 0. Daniel ruled on AB#501 that a command which displayed nothing must not report
    /// success, so the refusal is restated in <c>TwigCommands.ShowBatch</c>. This arm pins BOTH
    /// halves: the non-zero exit AND the message naming the two working spellings. Asserting
    /// only the exit code would pass against a refusal that tells the user nothing, and
    /// asserting only "some refusal happened" would pass against one wearing the workspace
    /// guard's wording (proven by mutation — <c>tools/ab501-mutants.sh</c> M5/M6).
    /// </remarks>
    [Fact]
    public async Task NoIdsAtAll_IsAUsageError_NamingBothSpellings()
    {
        var (exitCode, _, stderr) = await RunTwigAsync("show-batch");

        exitCode.ShouldBe(1, "a command that displayed nothing must not report success.");
        stderr.ShouldContain("twig show-batch <ids>",
            customMessage: "the refusal must name the positional spelling.");
        stderr.ShouldContain("--batch",
            customMessage: "the refusal must name the named spelling too.");
        stderr.ShouldNotContain("No twig workspace found",
            customMessage: "this is a missing-argument mistake, not a missing-workspace one; a "
                + "refusal wearing the other guard's wording sends the user to fix the wrong thing.");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunTwigAsync(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var twigAssembly = Path.Combine(
            repositoryRoot, "src", "Twig", "bin", configuration, "net11.0", "twig.dll");
        File.Exists(twigAssembly).ShouldBeTrue($"Twig CLI assembly not found at {twigAssembly}");

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = "dotnet";

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = _scratchRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(twigAssembly);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["TWIG_PAT"] = "test-pat";
        const string blockedProxy = "http://127.0.0.1:1";
        const string loopbackNoProxy = "127.0.0.1,localhost";
        startInfo.Environment["HTTP_PROXY"] = blockedProxy;
        startInfo.Environment["http_proxy"] = blockedProxy;
        startInfo.Environment["HTTPS_PROXY"] = blockedProxy;
        startInfo.Environment["https_proxy"] = blockedProxy;
        startInfo.Environment["NO_PROXY"] = loopbackNoProxy;
        startInfo.Environment["no_proxy"] = loopbackNoProxy;
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment.Remove("all_proxy");
        startInfo.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");

        using var process = System.Diagnostics.Process.Start(startInfo);
        process.ShouldNotBeNull();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>
/// AB#501 — the named/positional resolution rule for <c>show-batch</c>, pinned at the seam that
/// can actually observe it.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 This class exists BECAUSE of a mutation result, not on principle. Two mutants —
/// "the positional binds but its value is dropped" and "the positional wins over
/// <c>--batch</c>" — survived the entire production-CLI suite above, because those arms run
/// without a populated cache and the workspace refusal fires before the ids are read. Every
/// resolution produces the same bytes there, so no assertion at that layer can discriminate.
/// </para>
/// <para>
/// The remedy is the one the untestability points at: <see cref="TwigCommands.ResolveBatch"/>
/// is a pure function, so the rule can be asserted directly rather than inferred from output it
/// cannot reach.
/// </para>
/// </remarks>
public sealed class ShowBatchResolutionTests
{
    /// <summary>
    /// The positional's value REACHES the command — it is not merely bound and discarded.
    /// </summary>
    /// <remarks>
    /// Kills <c>tools/ab501-mutants.sh</c> M2. Asserts the exact value rather than
    /// non-null: a resolution returning <c>string.Empty</c> is bound-and-dropped, and
    /// <c>show-batch ""</c> exits 0 with an empty array — a silent false green.
    /// </remarks>
    [Theory]
    [InlineData("154")]
    [InlineData("154,140")]
    [InlineData("154,140,390")]
    public void PositionalValue_IsResolvedVerbatim(string ids)
        => TwigCommands.ResolveBatch(batch: null, batchArg: ids).ShouldBe(ids,
            customMessage: "the positional's value must reach the command intact; binding it and "
                + "then dropping it exits 0 having displayed nothing.");

    /// <summary>The named option's value reaches the command unchanged.</summary>
    [Theory]
    [InlineData("154")]
    [InlineData("154,140,390")]
    public void NamedValue_IsResolvedVerbatim(string ids)
        => TwigCommands.ResolveBatch(batch: ids, batchArg: null).ShouldBe(ids,
            customMessage: "--batch was never broken and must stay that way.");

    /// <summary>
    /// The NAMED option wins when both are supplied.
    /// </summary>
    /// <remarks>
    /// Kills <c>tools/ab501-mutants.sh</c> M3. The fixture makes the two sources DISAGREE, which
    /// is what makes the arm discriminating: with both set to the same value it would pass
    /// against either precedence. Matches every other twin on this surface
    /// (<c>text ?? textArg</c>, <c>org ?? orgArg</c>) — AB#398's inherited rule that positionals
    /// are added beside named options, never substituted for them.
    /// </remarks>
    [Fact]
    public void NamedOption_WinsOverThePositional()
        => TwigCommands.ResolveBatch(batch: "154,140", batchArg: "999").ShouldBe("154,140",
            customMessage: "--batch must win; inverting this is the substitution direction that "
                + "regressed edit --field and init --org/--project on AB#398.");

    /// <summary>
    /// Neither supplied resolves to null, which is what the usage refusal keys on.
    /// </summary>
    /// <remarks>
    /// The negative control. Without it a resolution returning <c>string.Empty</c> for "nothing
    /// supplied" would satisfy every arm above while turning the refusal into an exit-0 empty
    /// array — the exact false green Daniel's AB#501 ruling rejected.
    /// </remarks>
    [Fact]
    public void NeitherSupplied_ResolvesToNull_SoTheUsageRefusalCanFire()
        => TwigCommands.ResolveBatch(batch: null, batchArg: null).ShouldBeNull(
            "the refusal keys on null; any other sentinel makes 'no ids' exit 0 with '[]'.");
}
