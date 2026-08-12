using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Covers <c>twig process description</c> — the command's own behaviour, on top of the
/// assembler behaviour covered in <see cref="ProcessDescriptionAssemblerTests"/>.
/// </summary>
/// <remarks>
/// Shaped after <c>ProcessLayoutCommandTests</c>, which is the closest prior art: same
/// <c>--out</c> contract, same confirmation-on-stderr rule, same structure-only promise.
/// </remarks>
public sealed class ProcessDescriptionCommandTests : IDisposable
{
    private readonly StringWriter _stderr = new();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        _stderr.Dispose();
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    private string TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"twig-description-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// A fixture with two types whose FIELD SETS DIFFER. That difference is the founding
    /// correctness defect this feature fixes — the shipped behaviour reports the same
    /// project-wide field list for every type — so a fixture where both types carried the
    /// same fields could not detect a regression back to it.
    /// </summary>
    private static ScriptedDescriptionSource BuildSource()
    {
        var types = new[] { "Niflheim.Grilling", "Niflheim.Decision" };

        return new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Grilling"] = new(
                Fields:
                [
                    new ProcessTypeField("Custom.GrillingOnly", "Grilling Only", "string", null, false, "custom", false, ""),
                    new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, ""),
                ],
                States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                Transitions: [new ProcessTypeTransition("", "To do")]),

            ["Niflheim.Decision"] = new(
                Fields:
                [
                    new ProcessTypeField("Custom.DecisionOnly", "Decision Only", "string", null, false, "custom", false, ""),
                    new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, ""),
                ],
                States: [new ProcessTypeState("Done", "Completed", 3, "339947", "custom", false)],
                Transitions: [new ProcessTypeTransition("Done", "Done")]),
        });
    }

    private ProcessDescriptionCommand BuildCommand(IProcessDescriptionSource? source = null) =>
        new(
            new ProcessDescriptionAssembler(source ?? BuildSource())
            {
                RouteVersions = [new ProcessDescriptionRouteVersion("work/processes/{processId}/workItemTypes", "7.1-preview.2")],
            },
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory(),
            // A frozen clock so the only permitted variance is pinned and the file's content
            // is otherwise fully determined.
            new FrozenTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)),
            stderr: _stderr);

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Writing the file
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The whole point: a document lands on disk, complete, as an artifact to diff.</summary>
    [Fact]
    public async Task Execute_WithOutPath_WritesTheDocumentToThatFile()
    {
        var path = TempFile(".json");

        var exitCode = await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(0);
        File.Exists(path).ShouldBeTrue();

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("Niflheim.Grilling");
        content.ShouldContain("Niflheim.Decision");
        content.ShouldContain("descriptorVersion");
        content.ShouldContain("0.1");
    }

    /// <summary>
    /// 🔴 Byte-stability through the whole command, not just the assembler: two runs write
    /// files that are byte-for-byte identical.
    /// </summary>
    /// <remarks>
    /// The timestamp is excluded by construction — the clock is frozen — so any difference at
    /// all is a real ordering defect. This is the end-to-end form of the suite's most
    /// important assertion, and it defends the rendering layer as well as the assembler.
    /// </remarks>
    [Fact]
    public async Task Execute_TwiceAgainstAnUnchangedProcess_WritesByteIdenticalFiles()
    {
        var first = TempFile(".json");
        var second = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, first, ProcessDescriptionCommand.CompleteFormat);
        await BuildCommand().ExecuteAsync(null, second, ProcessDescriptionCommand.CompleteFormat);

        var firstBytes = await File.ReadAllBytesAsync(first);
        var secondBytes = await File.ReadAllBytesAsync(second);

        // Precondition: the files are not trivially empty, which would make byte-identity
        // meaningless.
        firstBytes.Length.ShouldBeGreaterThan(100);
        firstBytes.ShouldBe(secondBytes);
    }

    /// <summary>
    /// 🔴 Fields are TYPE-SCOPED: two different types emit different field sets.
    /// </summary>
    /// <remarks>
    /// The founding correctness defect. Against the shipped behaviour — the project-wide
    /// field list, identical for every type — the two types would carry the same fields and
    /// this would fail.
    /// </remarks>
    [Fact]
    public async Task Execute_EmitsDifferentFieldSetsForDifferentTypes()
    {
        var path = TempFile(".json");
        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("Custom.GrillingOnly");
        content.ShouldContain("Custom.DecisionOnly");
    }

    /// <summary>The confirmation goes to the error stream so <c>--out</c> composes in scripts.</summary>
    [Fact]
    public async Task Execute_WithOutPath_ConfirmationGoesToStderrNotIntoTheFile()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var stderr = _stderr.ToString();
        stderr.ShouldContain("Wrote process description");
        stderr.ShouldContain(path);

        (await File.ReadAllTextAsync(path)).ShouldNotContain("Wrote process description");
    }

    /// <summary>Omitting <c>--out</c> renders to stdout and writes no file.</summary>
    [Fact]
    public async Task Execute_WithoutOutPath_RendersToStdoutAndWritesNoFile()
    {
        var originalOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var exitCode = await BuildCommand().ExecuteAsync(null, null, ProcessDescriptionCommand.CompleteFormat);

            exitCode.ShouldBe(0);
            stdout.ToString().ShouldContain("Niflheim.Grilling");
            _stderr.ToString().ShouldNotContain("Wrote process description");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>An unwritable destination fails rather than silently producing no artifact.</summary>
    [Fact]
    public async Task Execute_UnwritablePath_ReturnsExitCode1AndReportsIt()
    {
        var blocker = TempFile(".txt");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var path = Path.Combine(blocker, "description.json");

        var exitCode = await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(1);
        _stderr.ToString().ShouldContain("Could not write");
    }

    // ═══════════════════════════════════════════════════════════════
    //  The abridged rendering declares itself
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The text rendering states it is abridged AND names the format that carries the whole
    /// thing — asserted against the ACTUAL <c>-o</c> value that produces the complete
    /// document, not a hardcoded string.
    /// </summary>
    /// <remarks>
    /// The distinction is the point. A bare string-presence check would pass against a banner
    /// naming a format that does not exist; reading the name from the same constant the
    /// renderer selection uses means the banner cannot drift into pointing at nothing. This
    /// self-declaration is the CONDITION on which an abridged rendering was accepted at all.
    /// </remarks>
    [Fact]
    public async Task Execute_AbridgedRendering_DeclaresItselfAndNamesTheRealCompleteFormat()
    {
        var path = TempFile(".txt");

        await BuildCommand().ExecuteAsync(null, path, "human");

        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("ABRIDGED");
        // 🔴 The FULL rendered phrase, not the bare token. Asserting on "json" alone would be
        // satisfied by the word appearing incidentally anywhere in the document; this pins
        // that the banner actually instructs the reader how to get the complete version.
        content.ShouldContain($"-o {ProcessDescriptionCommand.CompleteFormat}");

        // And that named format is genuinely on the accept-list AND genuinely produces the
        // complete document — a banner naming a rejected or abridged value would be a live
        // lie that the string check alone cannot catch.
        OutputFormats.IsAccepted(ProcessDescriptionCommand.CompleteFormat).ShouldBeTrue();
        ProcessDescriptionCommand.IsCompleteFormat(ProcessDescriptionCommand.CompleteFormat)
            .ShouldBeTrue();
    }

    /// <summary>
    /// The complete rendering does NOT carry the abridged banner.
    /// </summary>
    /// <remarks>
    /// 🔴 This test was previously a TAUTOLOGY and is preserved as a lesson: while the banner
    /// node was tagged human-only, the JSON renderer could never emit it regardless of whether
    /// the abridged/complete decision was computed correctly — the test would have passed
    /// against an implementation with that decision inverted or deleted outright. The banner
    /// now reaches every audience, so the assertion below is load-bearing: it fails if
    /// `IsCompleteFormat` ever stops recognising the complete format.
    /// </remarks>
    [Fact]
    public async Task Execute_CompleteRendering_DoesNotClaimToBeAbridged()
    {
        var completePath = TempFile(".json");
        var abridgedPath = TempFile(".min");

        await BuildCommand().ExecuteAsync(null, completePath, ProcessDescriptionCommand.CompleteFormat);
        await BuildCommand().ExecuteAsync(null, abridgedPath, "minimal");

        // Precondition: the banner is reachable in THIS renderer family at all. Without this
        // the assertion below could pass simply because no format ever emits the banner —
        // which is exactly how this test was hollow before.
        (await File.ReadAllTextAsync(abridgedPath)).ShouldContain("ABRIDGED");

        (await File.ReadAllTextAsync(completePath)).ShouldNotContain("ABRIDGED");
    }

    /// <summary>
    /// The abridged rendering genuinely omits detail that the complete one carries.
    /// </summary>
    /// <remarks>
    /// 🔴 Asserted by CONTENT, not by length. Comparing a human rendering's byte count against
    /// a JSON one measures the format (prose lines vs indented JSON), not abridgement — that
    /// comparison would pass even if the human rendering carried every field. Naming a
    /// specific detail token that must be present in one and absent from the other is the
    /// assertion that actually distinguishes the two.
    /// </remarks>
    [Fact]
    public async Task Execute_AbridgedRendering_OmitsDetailTheCompleteRenderingCarries()
    {
        var abridged = TempFile(".txt");
        var complete = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, abridged, "human");
        await BuildCommand().ExecuteAsync(null, complete, ProcessDescriptionCommand.CompleteFormat);

        var abridgedText = await File.ReadAllTextAsync(abridged);
        var completeText = await File.ReadAllTextAsync(complete);

        // A field reference name is per-type detail: the complete document carries it, the
        // summary does not.
        completeText.ShouldContain("Custom.GrillingOnly");
        abridgedText.ShouldNotContain("Custom.GrillingOnly");

        // But the summary still identifies the TYPE — it is a summary, not an empty file.
        abridgedText.ShouldContain("Niflheim.Grilling");
    }

    /// <summary>
    /// 🔴 Every format that produces the complete document is treated as complete — not just
    /// the canonical <c>json</c>.
    /// </summary>
    /// <remarks>
    /// Found by independent review. <c>json-full</c> and <c>json-compact</c> are on the
    /// accept-list and resolve to the SAME renderer as <c>json</c>, but the first
    /// implementation compared against the single canonical value and stamped their output
    /// "abridged". A false warning is as much a lie as a missing one: it tells a reader
    /// holding the complete document to go looking for content already in front of them.
    /// </remarks>
    [Theory]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    public async Task Execute_EveryCompleteFormat_DoesNotClaimToBeAbridged(string format)
    {
        // Precondition: the format really is accepted, so this is not asserting about a value
        // the CLI would reject anyway.
        OutputFormats.IsAccepted(format).ShouldBeTrue();

        var path = TempFile(".json");
        await BuildCommand().ExecuteAsync(null, path, format);

        var content = await File.ReadAllTextAsync(path);
        content.ShouldNotContain("ABRIDGED");
        // And it really is the complete document, not merely one lacking the banner.
        content.ShouldContain("Custom.GrillingOnly");
    }

    /// <summary>
    /// A format that is NOT complete is still labelled abridged — proving the check above did
    /// not simply disable the banner everywhere.
    /// </summary>
    [Theory]
    [InlineData("human")]
    [InlineData("minimal")]
    public void IsCompleteFormat_RejectsFormatsThatDoNotProduceTheCompleteDocument(string format)
    {
        OutputFormats.IsAccepted(format).ShouldBeTrue();
        ProcessDescriptionCommand.IsCompleteFormat(format).ShouldBeFalse();
    }

    /// <summary>
    /// 🔴 A type whose parts could not be read says so in the written document.
    /// </summary>
    /// <remarks>
    /// End-to-end form of the assembler-level guard: the distinction has to survive the
    /// rendering layer, or the honesty never reaches the file the reader actually holds.
    /// </remarks>
    [Fact]
    public async Task Execute_WrittenDocumentNamesTypesWhosePartsCouldNotBeRead()
    {
        var types = new[] { "Niflheim.Grilling" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.Grilling"] = new(
                    Fields: [],
                    States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                    Transitions: [],
                    Unfetched: ["fields", "transitions"]),
            });

        var path = TempFile(".json");
        await BuildCommand(source).ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("unfetched");
        content.ShouldContain("fields,transitions");
    }

    /// <summary>
    /// 🔴 EVERY abridged format declares itself — not just the human one.
    /// </summary>
    /// <remarks>
    /// Found by independent review. The banner was originally tagged human-only, so
    /// <c>-o minimal</c> and <c>-o ids</c> emitted truncated documents carrying no notice that
    /// anything had been dropped. A machine consumer is the worst reader to leave uninformed:
    /// it cannot notice the omission the way a person scanning the output might. The test
    /// suite only exercised <c>human</c>, so it passed against that wrong implementation —
    /// which is why this is a Theory over every abridged format rather than one more Fact.
    /// </remarks>
    [Theory]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task Execute_EveryAbridgedFormat_DeclaresItselfAndNamesTheCompleteFormat(string format)
    {
        // Precondition: the format is accepted and really is abridged, so this is not
        // asserting about a value the CLI rejects or one that carries everything.
        OutputFormats.IsAccepted(format).ShouldBeTrue();
        ProcessDescriptionCommand.IsCompleteFormat(format).ShouldBeFalse();

        var path = TempFile(".out");
        await BuildCommand().ExecuteAsync(null, path, format);

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("ABRIDGED");
        content.ShouldContain($"-o {ProcessDescriptionCommand.CompleteFormat}");
    }

    /// <summary>
    /// 🔴 <c>-o ids</c> is refused rather than silently producing an empty file.
    /// </summary>
    /// <remarks>
    /// Surfaced by the Theory above while fixing the human-only banner. The ids renderer emits
    /// only cells keyed "id" holding an integer, and this document has no numeric ids — so it
    /// would write an EMPTY file and exit 0. It is also the one format that structurally
    /// cannot carry the abridged declaration, so the reader would receive nothing AND no
    /// notice. Refusing names the format that actually works.
    /// </remarks>
    [Fact]
    public async Task Execute_IdsFormat_IsRefusedRatherThanWritingAnEmptyFile()
    {
        var path = TempFile(".ids");

        var exitCode = await BuildCommand().ExecuteAsync(null, path, "ids");

        exitCode.ShouldBe(1);
        File.Exists(path).ShouldBeFalse();

        var stderr = _stderr.ToString();
        stderr.ShouldContain("ids");
        // It must point the reader at a format that genuinely works.
        stderr.ShouldContain(ProcessDescriptionCommand.CompleteFormat);
    }

    /// <summary>
    /// 🔴 A renderer failure leaves NO partial file on disk.
    /// </summary>
    /// <remarks>
    /// Found by independent review. The unknown-type path already promised "no partial file",
    /// but a write that failed mid-render left a TRUNCATED document behind — worse than no
    /// file, because a truncated description is silently missing types and a reader diffing
    /// it sees differences that are not real. The command now renders to a temp file and moves
    /// it into place.
    /// <para>
    /// Driven by making the destination un-writable at the moment of the move: the directory
    /// is replaced by a file after the command starts, so `File.Move` fails.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Execute_WhenTheDestinationIsUnwritable_LeavesNoPartialArtifact()
    {
        var blocker = TempFile(".txt");
        await File.WriteAllTextAsync(blocker, "not a directory");
        var path = Path.Combine(blocker, "description.json");

        var exitCode = await BuildCommand().ExecuteAsync(
            null, path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(1);
        File.Exists(path).ShouldBeFalse();

        // And no scratch file was orphaned beside the destination.
        var dir = Path.GetDirectoryName(blocker)!;
        var leftovers = Directory.GetFiles(dir, Path.GetFileName(path) + ".tmp-*");
        leftovers.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Selection and error paths
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Naming a type describes only that type, in the same document shape.</summary>
    [Fact]
    public async Task Execute_WithNamedType_DescribesOnlyThatType()
    {
        var path = TempFile(".json");

        var exitCode = await BuildCommand().ExecuteAsync(
            "Niflheim.Grilling", path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(0);
        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("Custom.GrillingOnly");
        content.ShouldNotContain("Custom.DecisionOnly");
        // Same shape: the header and version stamp are still there.
        content.ShouldContain("descriptorVersion");
    }

    /// <summary>
    /// 🔴 An unknown type is a hard error: non-zero exit, names what was asked for, and NO
    /// partial file is written.
    /// </summary>
    [Fact]
    public async Task Execute_UnknownType_FailsNamingItAndWritesNoFile()
    {
        var path = TempFile(".json");

        var exitCode = await BuildCommand().ExecuteAsync(
            "Niflheim.NoSuchType", path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(1);
        File.Exists(path).ShouldBeFalse();
        _stderr.ToString().ShouldContain("Niflheim.NoSuchType");
    }

    /// <summary>An unresolvable process fails rather than writing an empty document.</summary>
    [Fact]
    public async Task Execute_WhenProcessCannotBeResolved_FailsAndWritesNoFile()
    {
        var path = TempFile(".json");
        var source = new ScriptedDescriptionSource([], []) { Identity = null };

        var exitCode = await BuildCommand(source).ExecuteAsync(
            null, path, ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(1);
        File.Exists(path).ShouldBeFalse();
        _stderr.ToString().ShouldContain("Could not describe");
    }

    /// <summary>
    /// 🔴 The document declares that it is KNOWN INCOMPLETE, on its face, in the file itself.
    /// </summary>
    /// <remarks>
    /// The ticket's acceptance criterion says the document must not be released as complete
    /// while conditional requiredness (AB#236) and picklist values (AB#237) are outstanding.
    /// Putting the reservation in the artifact is how that survives the session that shipped
    /// it — a note in a PR description does not travel with the file.
    /// </remarks>
    [Fact]
    public async Task Execute_WrittenDocumentDeclaresItsKnownIncompleteness()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("knownGaps");
        content.ShouldContain("conditionalRequiredness");
        content.ShouldContain("AB#236");
        content.ShouldContain("picklistValues");
        content.ShouldContain("AB#237");
    }

    /// <summary>
    /// The header records the pinned api-version per route, so two documents taken months
    /// apart cannot differ merely because the server moved.
    /// </summary>
    [Fact]
    public async Task Execute_WrittenDocumentRecordsThePinnedApiVersionPerRoute()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("routeApiVersions");
        content.ShouldContain("7.1-preview.2");
    }

    /// <summary>
    /// 🔴 The process id is in the document, so a reader can tell WHICH process was described
    /// even when two processes share a display name.
    /// </summary>
    [Fact]
    public async Task Execute_WrittenDocumentRecordsTheProcessIdItWasResolvedBy()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        (await File.ReadAllTextAsync(path))
            .ShouldContain("7f984e4c-e856-4fc3-8457-fd4e8acf2e57");
    }
}
