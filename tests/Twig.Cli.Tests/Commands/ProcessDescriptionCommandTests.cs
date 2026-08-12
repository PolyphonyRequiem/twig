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
        // 🔴 Read from the constant, so the assertion tracks the real complete format.
        content.ShouldContain(ProcessDescriptionCommand.CompleteFormat);

        // And that named format is genuinely on the accept-list — a banner naming a rejected
        // value would be a live lie the check above alone would not catch.
        OutputFormats.IsAccepted(ProcessDescriptionCommand.CompleteFormat).ShouldBeTrue();
    }

    /// <summary>
    /// The complete rendering does NOT carry the abridged banner — otherwise the banner is
    /// decoration rather than a claim about the document in hand.
    /// </summary>
    [Fact]
    public async Task Execute_CompleteRendering_DoesNotClaimToBeAbridged()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        (await File.ReadAllTextAsync(path)).ShouldNotContain("ABRIDGED");
    }

    /// <summary>
    /// The abridged rendering really is shorter than the complete one — proving the banner is
    /// telling the truth rather than being attached to an identical document.
    /// </summary>
    [Fact]
    public async Task Execute_AbridgedRendering_IsActuallyShorterThanTheCompleteOne()
    {
        var abridged = TempFile(".txt");
        var complete = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, abridged, "human");
        await BuildCommand().ExecuteAsync(null, complete, ProcessDescriptionCommand.CompleteFormat);

        var abridgedLength = (await File.ReadAllTextAsync(abridged)).Length;
        var completeLength = (await File.ReadAllTextAsync(complete)).Length;

        abridgedLength.ShouldBeLessThan(completeLength);
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
