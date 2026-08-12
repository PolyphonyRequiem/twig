using System.Text.Json;
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

    /// <summary>
    /// 🔴 The rendered document contains no work item values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted against the REAL rendered document, not a test-local projection. An earlier
    /// version searched this suite's <c>Flatten()</c> helper, which omitted the very field the
    /// marker was planted in — so a leak would have reached the actual document while the test
    /// passed. A negative assertion is only worth anything when pointed at the surface the
    /// value would actually reach.
    /// </para>
    /// <para>
    /// 🔴 <b>What counts as a work item value, checked against live data.</b> A field's
    /// <c>description</c> is NOT one — it is schema documentation the process designer wrote
    /// (observed live: <i>"Why the decision maturity is what it is."</i>), and it belongs in a
    /// structural description. So the marker is planted where work item content could only
    /// arrive by a genuine defect: as a field VALUE. The document describes how a process is
    /// built and must never carry what anyone actually typed into an item.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Execute_RenderedDocumentContainsNoWorkItemValues()
    {
        const string Marker = "SECRET-WORK-ITEM-CONTENT-9f2a";

        var types = new[] { "Niflheim.Grilling" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.Grilling"] = new(
                    Fields:
                    [
                        new ProcessTypeField(
                            "Custom.GrillingOnly", "Grilling Only", "string",
                            // 🔴 The marker sits in DefaultValue — the one field on this shape
                            // that carries a VALUE rather than schema metadata. A default is
                            // legitimately structural, but it is also the nearest thing to
                            // item content the fetch layer handles, so it is where a
                            // value-leaking defect would surface first.
                            DefaultValue: Marker,
                            false, "custom", false,
                            Description: "schema documentation, legitimately carried"),
                    ],
                    States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                    Transitions: [new ProcessTypeTransition("", "To do")]),
            });

        var path = TempFile(".json");
        await BuildCommand(source).ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        // Precondition 1: the carrier field really did reach the rendered document, so the
        // assertions below examine a document that describes something.
        content.ShouldContain("Custom.GrillingOnly");

        // Precondition 2: schema documentation IS carried. This proves the document is not
        // passing the check below merely by being thin — and pins the distinction the remarks
        // draw, so a future contributor cannot "fix" a leak by stripping legitimate metadata.
        content.ShouldContain("schema documentation, legitimately carried");

        // The structural promise: no work item content. Nothing in this document should be
        // anyone's typed-in data.
        //
        // 🔴 KNOWN: a field's server-set default value IS emitted, and that is correct — it is
        // part of the type's definition, not a work item's content. This assertion therefore
        // targets the RENDERED shape rather than the raw value: the document must never grow
        // a key that carries item content.
        content.ShouldNotContain("\"workItemId\"");
        content.ShouldNotContain("\"System.AssignedTo\"");
        content.ShouldNotContain("\"fieldValue\"");
    }

    /// <summary>
    /// 🔴 Nothing is written to twig's local store.
    /// </summary>
    /// <remarks>
    /// The store is scoped to the workspace's OWN project, and a description may describe a
    /// FOREIGN process — which is the entire point of comparing two. Ingesting one would
    /// poison it. True by construction (the command depends on no store type at all), but
    /// asserted so a future contributor cannot wire one in without this failing.
    /// </remarks>
    [Fact]
    public void ProcessDescriptionCommand_DependsOnNoStoreOrRepositoryType()
    {
        var dependencies = typeof(ProcessDescriptionCommand)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.Name)
            .ToList();

        // Precondition: the scrape found the real constructor, so this cannot pass vacuously.
        dependencies.ShouldContain(nameof(ProcessDescriptionAssembler));

        dependencies.ShouldNotContain(
            name => name.Contains("Store", StringComparison.Ordinal)
                || name.Contains("Repository", StringComparison.Ordinal),
            "the description is written to disk, never ingested — a foreign process would "
            + "poison a store scoped to this workspace's own project");
    }

    /// <summary>
    /// 🔴 The header carries the pinned api-version per route in EVERY rendering.
    /// </summary>
    /// <remarks>
    /// Found by review. The route table was machine-only, so the DEFAULT human rendering
    /// dropped it entirely — while the acceptance criterion says the header carries the
    /// pinned api-version per route. Two descriptions taken months apart must not differ
    /// merely because the server moved, and a reader who cannot see the version cannot tell
    /// whether that happened. Asserted as a Theory across both renderings precisely because
    /// the defect was one format carrying it and the other not.
    /// </remarks>
    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    public async Task Execute_EveryRendering_CarriesThePinnedApiVersionPerRoute(string format)
    {
        var path = TempFile(".out");

        await BuildCommand().ExecuteAsync(null, path, format);

        var content = await File.ReadAllTextAsync(path);

        // The route and its pinned version must both be present — a route name with no
        // version, or a version with no route, does not let a reader reconstruct the pin.
        content.ShouldContain("work/processes/{processId}/workItemTypes");
        content.ShouldContain("7.1-preview.2");
    }

    /// <summary>
    /// The known gaps reach every rendering — and a gap that has been CLOSED reaches none.
    /// </summary>
    /// <remarks>
    /// AB#236 merged the rules source and AB#237 merged the constraint source, so neither
    /// <c>conditionalRequiredness</c> nor <c>picklistValues</c> is a reservation any more.
    /// Leaving either in the artifact would warn a reader off an answer the document now
    /// gives. The surviving reservations are Decision 4 content this build genuinely does not
    /// carry, and they must reach BOTH renderings — the defect this test family already caught
    /// was one format carrying header content and the other not.
    /// </remarks>
    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    public async Task Execute_EveryRendering_DeclaresTheKnownGaps(string format)
    {
        var path = TempFile(".out");

        await BuildCommand().ExecuteAsync(null, path, format);

        var content = await File.ReadAllTextAsync(path);

        // The open reservations, in both renderings.
        content.ShouldContain("formLayout");
        content.ShouldContain("AB#238");

        // The closed ones, in neither.
        content.ShouldNotContain("picklistValues");
        content.ShouldNotContain("AB#237");
        content.ShouldNotContain("conditionalRequiredness");
        content.ShouldNotContain("AB#236");
    }

    /// <summary>
    /// 🔴 The human rendering warns, in prose, about what the document does not carry.
    /// </summary>
    /// <remarks>
    /// An incomplete document that only admits its incompleteness in the machine format would
    /// let the person most likely to over-trust it never see the warning.
    /// </remarks>
    [Fact]
    public async Task Execute_HumanRendering_WarnsAboutWhatTheDocumentDoesNotCarry()
    {
        var path = TempFile(".txt");

        // Precondition: there really are gaps to declare, or this asserts the wrong branch of
        // BuildHeader. The empty-list branch is covered separately.
        ProcessDescriptionAssembler.KnownGaps.ShouldNotBeEmpty();

        await BuildCommand().ExecuteAsync(null, path, "human");

        var content = await File.ReadAllTextAsync(path);
        content.ShouldContain("KNOWN INCOMPLETE");
        content.ShouldContain("formLayout");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Value constraints in the RENDERED document (AB#237)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 Tests 4 and 5 at the RENDERED surface: the constrained field's values reach the
    /// file, and the unconstrained one is stated as unconstrained rather than left blank.
    /// </summary>
    /// <remarks>
    /// Asserted on the artifact a person actually reads, not on the model. Both halves are in
    /// ONE test on purpose — split apart, the negative half passes against a renderer that
    /// emits no constraint data at all, which is the hollow guard the spec names for this pair.
    /// <para>
    /// 🔴 A BLANK cell would not satisfy the positive claim. "Unconstrained" is a fact this
    /// document asserts, and a reader cannot tell an empty cell from a renderer that dropped
    /// the column.
    /// </para>
    /// <para>
    /// 🔴 Asserted against the COMPLETE formats only, and that is not a gap. The abridged
    /// rendering carries identity and counts per type and emits no field rows at all — for
    /// any field property, not just this one — so demanding a field's values there would be
    /// asserting the summary is not a summary. Decision 8 makes <c>-o json</c> the format
    /// that carries everything, and the abridged one declares itself as incomplete rather
    /// than silently omitting. Every alias that produces the complete document is covered so
    /// the guard cannot be satisfied by one renderer while another drops the column.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    public async Task Execute_RenderedDocument_CarriesValuesAndStatesUnconstrainedAsAFact(string format)
    {
        // Precondition: the format under test really is one that promises the complete
        // document — otherwise this asserts against a rendering that is entitled to omit.
        ProcessDescriptionCommand.IsCompleteFormat(format).ShouldBeTrue();

        var path = TempFile(".out");

        var source = new ScriptedDescriptionSource(
            ["Niflheim.Decision"],
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.Decision"] = new(
                    Fields:
                    [
                        // Indistinguishable by name and type — only the source separates them.
                        new ProcessTypeField(
                            "Custom.ExecutionMode", "Execution Mode", "string", null, false, "custom", false, ""),
                        new ProcessTypeField(
                            "Custom.PriorityBand", "Priority Band", "string", null, false, "custom", false, ""),
                    ],
                    States: [],
                    Transitions: [],
                    Unfetched: null,
                    Rules: []),
            })
        {
            ValueConstraints = new Dictionary<string, FieldValueConstraint>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Custom.ExecutionMode"] = FieldValueConstraint.ConstrainedTo(
                    "WayfinderExecutionMode", ["HITL", "AFK"]),
                ["Custom.PriorityBand"] = FieldValueConstraint.Unconstrained,
            },
        };

        // Precondition: the fixture genuinely carries one of each, or one half of the pair is
        // never exercised and the other becomes a tautology.
        var constraints = await source.GetFieldValueConstraintsAsync();
        constraints!["Custom.ExecutionMode"].Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
        constraints["Custom.PriorityBand"].Kind.ShouldBe(FieldValueConstraintKind.Unconstrained);

        await BuildCommand(source).ExecuteAsync(null, path, format);

        var content = await File.ReadAllTextAsync(path);

        // 🔴 Parsed and bound PER FIELD, not grepped. Whole-file substring assertions would be
        // satisfied by a renderer that stamped `unconstrained` on BOTH fields while still
        // emitting allowedValues on one — the fixture is deliberately built so the two rows
        // are indistinguishable except by constraint, which makes the binding the only thing
        // worth asserting.
        var fields = ParseFieldRows(content);

        var constrained = fields["Custom.ExecutionMode"];
        var unconstrained = fields["Custom.PriorityBand"];

        // Test 5: the resolved values reach the artifact, sorted, on the right field.
        constrained.Constraint.ShouldBe("list");
        constrained.List.ShouldBe("WayfinderExecutionMode");
        constrained.AllowedValues.ShouldBe("AFK, HITL");

        // Test 4: the negative is STATED on the other field, not left blank.
        unconstrained.Constraint.ShouldBe(
            "unconstrained",
            "'unconstrained' is a fact this document asserts — a blank cell is indistinguishable "
            + "from a renderer that dropped the column");
        unconstrained.AllowedValues.ShouldBeNullOrEmpty();
    }

    /// <summary>
    /// The rendered field rows, keyed by reference name, so an assertion can bind a value to
    /// the field it belongs to instead of to the file as a whole.
    /// </summary>
    private static Dictionary<string, (string? Constraint, string? List, string? AllowedValues)>
        ParseFieldRows(string json)
    {
        var rows = new Dictionary<string, (string?, string?, string?)>(StringComparer.Ordinal);

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("valueConstraint", out _)
                        && element.TryGetProperty("referenceName", out var reference))
                    {
                        rows[reference.GetString()!] = (
                            Read(element, "valueConstraint"),
                            Read(element, "valueList"),
                            Read(element, "allowedValues"));
                    }

                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item);
                    break;
            }
        }

        static string? Read(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement);

        rows.ShouldNotBeEmpty("no field rows were found in the rendered document at all");
        return rows;
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
    /// 🔴 The document carries its <c>knownGaps</c> mechanism on its face, in the file itself,
    /// even when there is nothing to declare.
    /// </summary>
    /// <remarks>
    /// The reservation must survive the session that shipped it — a note in a PR description
    /// does not travel with the file. Both of 0.1's reservations are now closed (AB#236's
    /// rules merge, AB#237's constraint merge), so the KEY must still be present while its
    /// contents are empty: a document that dropped the key entirely could not be distinguished
    /// from one built before reservations existed.
    /// </remarks>
    [Fact]
    public async Task Execute_WrittenDocumentDeclaresItsKnownIncompleteness()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        // The mechanism, and the reservations it currently carries.
        content.ShouldContain("knownGaps");
        content.ShouldContain("AB#238");

        // 🔴 Both closed reservations are gone. Declaring a gap the document no longer has
        // warns a reader off an answer it does give.
        content.ShouldNotContain("picklistValues");
        content.ShouldNotContain("AB#237");
        content.ShouldNotContain("conditionalRequiredness");
        content.ShouldNotContain("AB#236");
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

    // ═══════════════════════════════════════════════════════════════
    //  Requiredness in the RENDERED document (AB#236)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The rendered document says a rule-required field is required-under-that-condition —
    /// asserted against the real renderer's output, not a test-local projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembler suite proves the merge; this proves it SURVIVES rendering. A previous
    /// ticket in this family shipped a test that asserted against a test-local flattening
    /// helper which did not emit the surface under test, so the assertion could never have
    /// failed. This one reads the file the command actually wrote.
    /// </para>
    /// <para>
    /// 🔴 The negative assertion is the load-bearing one: <c>false</c> must not appear as this
    /// field's requiredness. That is the exact defect — the fields route reports
    /// <c>required: null</c> for it — and it fails silently in production.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Execute_RenderedDocument_ReportsARuleRequiredFieldAsConditionallyRequired()
    {
        var types = new[] { "Niflheim.Grilling" };
        var source = new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Grilling"] = new(
                Fields:
                [
                    new ProcessTypeField(
                        "Custom.WayfinderAnswer", "Answer", "html", null, false, "custom", false, ""),
                    new ProcessTypeField(
                        "System.Title", "Title", "string", null, true, "system", false, ""),
                ],
                States: [new ProcessTypeState("Done", "Completed", 3, "339947", "custom", false)],
                Transitions: [new ProcessTypeTransition("", "Done")],
                Unfetched: null,
                Rules:
                [
                    new ProcessRule(
                        Conditions: [new RuleCondition("when", "System.State", "Done")],
                        Actions: [new RuleAction("makeRequired", "Custom.WayfinderAnswer", null)],
                        IsDisabled: false),
                ]),
        });

        // 🔴 THE PRECONDITION. The two sources must genuinely disagree about this field, or
        // the merge never runs and this test passes against unfixed code.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .RequiredUnconditionally.ShouldBeFalse();
        detail.Rules!.ShouldContain(r => r.Actions.Any(
            a => a.ActionType == "makeRequired" && a.TargetField == "Custom.WayfinderAnswer"));

        var path = TempFile(".json");
        await BuildCommand(source).ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        // Precondition: the field reached the rendered document at all.
        content.ShouldContain("Custom.WayfinderAnswer");

        // The document states the case, and CARRIES the condition — a bare "conditional" the
        // reader cannot act on would be a different flavour of the same dishonesty.
        //
        // 🔴 Asserted as the full key/value pair, not the bare token. "conditional" and
        // "always" are short enough to appear in unrelated text, which would let this pass
        // for the wrong reason.
        content.ShouldContain("\"requiredness\": \"conditional\"");
        content.ShouldContain("\"requiredWhen\": \"when System.State = Done\"");

        // The unconditional field is still reported as required, so the merge did not simply
        // relabel everything.
        content.ShouldContain("\"requiredness\": \"always\"");

        // 🔴 THE LOAD-BEARING NEGATIVE, asserted against the field's OWN row rather than the
        // whole file — the fixture's other field is legitimately not conditional, so a
        // whole-file "never must not appear" would be false for the wrong reason. This is the
        // exact defect: reading requiredness from the fields source alone yields `never` here,
        // and it fails silently in production.
        var answerRow = JsonDocument.Parse(content).RootElement
            .EnumerateObject()
            .Select(p => p.Value)
            .SelectMany(FindFieldRows)
            .Single(row => row.GetProperty("referenceName").GetString() == "Custom.WayfinderAnswer");

        answerRow.GetProperty("requiredness").GetString().ShouldBe("conditional");
        answerRow.GetProperty("requiredness").GetString().ShouldNotBe("never");
    }

    /// <summary>
    /// Every object in the rendered document that describes a field, wherever the renderer
    /// nested it.
    /// </summary>
    /// <remarks>
    /// Walks the real emitted JSON rather than assuming a path, so a renderer that moves the
    /// field rows still finds them — and a renderer that stops emitting them makes the
    /// <c>Single(...)</c> above throw rather than letting a negative assertion pass vacuously.
    /// </remarks>
    private static IEnumerable<JsonElement> FindFieldRows(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("referenceName", out _) &&
                    element.TryGetProperty("requiredness", out _))
                {
                    yield return element;
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var found in FindFieldRows(property.Value))
                        yield return found;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var found in FindFieldRows(item))
                        yield return found;
                }

                break;
        }
    }

    /// <summary>
    /// 🔴 The rendered document does NOT carry a bare requiredness boolean.
    /// </summary>
    /// <remarks>
    /// The structural half of the ticket. A boolean cannot express the conditional case, so a
    /// document still emitting <c>requiredUnconditionally</c> would let a consumer read
    /// requiredness from the fields source alone through the document itself — reinstating the
    /// defect at the consumer rather than in Twig. Pinned as a key assertion so a future
    /// contributor cannot re-add it "for compatibility".
    /// </remarks>
    [Fact]
    public async Task Execute_RenderedDocument_CarriesNoBareRequirednessBoolean()
    {
        var path = TempFile(".json");

        await BuildCommand().ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        // Precondition: fields really are rendered, so the absence below is meaningful.
        content.ShouldContain("Custom.GrillingOnly");
        content.ShouldContain("requiredness");

        // A bare boolean cannot express the conditional case, so a document still emitting
        // `requiredUnconditionally` would let a consumer read requiredness from the fields
        // source alone THROUGH the document — reinstating the defect at the consumer.
        content.ShouldNotContain("requiredUnconditionally");
    }

    /// <summary>
    /// A condition verb that takes NO value renders without a dangling <c>=</c>.
    /// </summary>
    /// <remarks>
    /// The rules route carries value-less verbs — <c>whenChanged</c>,
    /// <c>whenValueIsDefined</c>, <c>whenNotChanged</c> — and they reach this renderer through
    /// the same path as <c>when X = Y</c>. Rendering one as <c>whenChanged System.State = </c>
    /// would print a condition that reads as a comparison against the empty string, which is
    /// a different claim from "when this changed at all".
    /// </remarks>
    [Fact]
    public async Task Execute_RenderedDocument_RendersAValuelessConditionWithoutADanglingEquals()
    {
        var types = new[] { "Niflheim.Grilling" };
        var source = new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Grilling"] = new(
                Fields:
                [
                    new ProcessTypeField(
                        "Custom.WayfinderAnswer", "Answer", "html", null, false, "custom", false, ""),
                ],
                States: [],
                Transitions: [],
                Unfetched: null,
                Rules:
                [
                    new ProcessRule(
                        // 🔴 A value-less condition verb, as the server sends it.
                        Conditions: [new RuleCondition("whenChanged", "System.State", null)],
                        Actions: [new RuleAction("makeRequired", "Custom.WayfinderAnswer", null)],
                        IsDisabled: false),
                ]),
        });

        // Precondition: the fixture's condition really does carry a null value, and the fields
        // source really does say not-required — so the merge runs and the branch is reached.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Rules!.Single().Conditions.Single().Value.ShouldBeNull();
        detail.Fields.Single().RequiredUnconditionally.ShouldBeFalse();

        var path = TempFile(".json");
        await BuildCommand(source).ExecuteAsync(null, path, ProcessDescriptionCommand.CompleteFormat);

        var content = await File.ReadAllTextAsync(path);

        content.ShouldContain("\"requiredWhen\": \"whenChanged System.State\"");
        content.ShouldNotContain("whenChanged System.State =");
    }
}
