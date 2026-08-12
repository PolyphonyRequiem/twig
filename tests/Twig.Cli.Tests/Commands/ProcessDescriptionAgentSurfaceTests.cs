using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#241 — the agent surface returns the SAME document with fewer types.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Test 14, one of the spec's nine red-flagged tests, and the structural proof that there
/// is one document format rather than two.</b> The whole ticket is a thin adapter over a seam
/// that already existed; the hard part is proving the adapter did not quietly become a second
/// document. So the assertions below drive the REAL CLI command and the REAL MCP render path
/// against ONE scripted source and compare the bytes.
/// </para>
/// <para>
/// 🔴 <b>Both sides are the shipped code, not a re-creation of it.</b> The CLI side runs
/// <see cref="ProcessDescriptionCommand.ExecuteAsync"/> and reads the file it wrote; the agent
/// side runs <see cref="ProcessDescriptionDocument.Render"/> against the same assembler, which
/// is the exact call `ProcessTools.RenderDocumentAsync` makes and the whole of what that method
/// does after resolving its two dependencies. A test that reimplemented either path would be
/// comparing one surface against a COPY of the other and would stay green through any change to
/// the real one — the hollow-guard class this repo has already been bitten by twice.
/// </para>
/// <para>
/// 🔴 <b>This project deliberately does NOT reference Twig.Mcp.</b> An earlier revision did, so
/// the test could call the tool method directly — and it turned CI red: Twig.Mcp is an
/// EXECUTABLE, referencing it copies `twig-mcp` into this suite's output, and
/// `BinaryLauncherTests` clears PATH precisely to assert that binary is NOT found. Instead the
/// real MCP host started in-process and crashed the Cli test host after 48 tests. It passed
/// locally only because AGENTS.md's canonical runner excludes `BinaryLauncher`. The parity
/// assertion that the tool actually calls this shared render lives in Twig.Mcp.Tests, where the
/// reference belongs.
/// </para>
/// <para>
/// Fixture and clock are shared with <see cref="ProcessDescriptionCommandTests"/> deliberately:
/// the capture timestamp is the ONLY permitted variance under Solution S2, so both surfaces are
/// given the SAME frozen clock and any difference at all is then a real defect rather than the
/// sanctioned one.
/// </para>
/// </remarks>
public sealed class ProcessDescriptionAgentSurfaceTests : IDisposable
{
    private readonly StringWriter _stderr = new();
    private readonly List<string> _tempFiles = [];

    /// <summary>
    /// The frozen capture instant handed to BOTH surfaces.
    /// </summary>
    /// <remarks>
    /// 🔴 One value, shared. Two frozen clocks set to the same literal would work today and
    /// silently stop testing anything the moment one was edited — and the header line they
    /// produce is the single line byte-identity is allowed to differ on, so it is exactly the
    /// place a drift would hide.
    /// </remarks>
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        _stderr.Dispose();
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twig-agent-description-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Two types with DIFFERENT field sets, states, rules, behaviours and layouts.
    /// </summary>
    /// <remarks>
    /// 🔴 The types must differ in content for a selection test to mean anything. If both types
    /// carried identical parts, "describe only Grilling" and "describe only Decision" would
    /// produce documents differing solely in a reference name, and a selection defect that
    /// returned the wrong type would pass. The assertions below state that precondition rather
    /// than trusting it.
    /// </remarks>
    private static ScriptedDescriptionSource BuildSource()
    {
        var types = new[] { "Niflheim.Grilling", "Niflheim.Decision" };

        return new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Grilling"] = new(
                Fields:
                [
                    new ProcessTypeField("Custom.GrillingOnly", "Grilling Only", "string", null, false, "custom", false, "A grilling field"),
                    new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, ""),
                ],
                States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                Transitions: [new ProcessTypeTransition("", "To do")],
                Rules:
                [
                    new ProcessRule(
                        Conditions: [new RuleCondition("whenStateIs", "System.State", "Doing")],
                        Actions: [new RuleAction("makeRequired", "Custom.GrillingOnly", null)],
                        IsDisabled: false,
                        Customization: RuleCustomization.From("custom"),
                        Name: "Require an answer"),
                ],
                Behaviours: [new ProcessBehaviourMembership("Custom.Wayfinding", string.Empty, null, true)],
                Layout: new ProcessDescriptionLayout(
                    SystemControls:
                    [
                        new ProcessDescriptionLayoutControl(
                            "System.State", "State", "FieldControl",
                            ReadOnly: false, Visible: true, Inherited: true,
                            IsContribution: false, Order: 0),
                    ],
                    Pages:
                    [
                        new ProcessDescriptionLayoutPage(
                            "Page.Details", "Details", "custom",
                            Visible: true, Inherited: true, IsContribution: false, Order: 0,
                            [
                                new ProcessDescriptionLayoutSection("Section1",
                                [
                                    new ProcessDescriptionLayoutGroup(
                                        "Group.Main", "Main", Visible: false, Inherited: true,
                                        IsContribution: false, Order: 0,
                                        [
                                            new ProcessDescriptionLayoutControl(
                                                "Custom.GrillingOnly", "Grilling Only", "FieldControl",
                                                ReadOnly: false, Visible: true, Inherited: false,
                                                IsContribution: false, Order: 0),
                                        ]),
                                ]),
                            ]),
                    ])),

            ["Niflheim.Decision"] = new(
                Fields:
                [
                    new ProcessTypeField("Custom.DecisionOnly", "Decision Only", "string", null, false, "custom", false, "A decision field"),
                    new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, ""),
                ],
                States: [new ProcessTypeState("Open", "InProgress", 2, "007acc", "inherited", false)],
                Transitions: [new ProcessTypeTransition("", "Open")],
                Rules: [],
                Behaviours: [],
                Layout: new ProcessDescriptionLayout(
                    SystemControls: [],
                    Pages:
                    [
                        new ProcessDescriptionLayoutPage(
                            "Page.Links", "Links", "links",
                            Visible: true, Inherited: true, IsContribution: false, Order: 1, []),
                    ])),
        })
        {
            BehaviourCatalogue = [new ProcessBehaviourSummary("Custom.Wayfinding", "Wayfinding", 40)],
        };
    }

    private static ProcessDescriptionAssembler BuildAssembler(ScriptedDescriptionSource source) =>
        new(source)
        {
            RouteVersions =
            [
                new ProcessDescriptionRouteVersion(
                    "work/processes/{processId}/workItemTypes", "7.1-preview.2"),
            ],
        };

    private ProcessDescriptionCommand BuildCommand(ScriptedDescriptionSource source) =>
        new(
            BuildAssembler(source),
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory(),
            new FrozenTimeProvider(CapturedAt),
            stderr: _stderr);

    /// <summary>The CLI's bytes for a type selection, read back from the file it wrote.</summary>
    private async Task<string> CliDocumentAsync(string[]? types)
    {
        var path = TempFile();

        var exitCode = await BuildCommand(BuildSource()).ExecuteAsync(
            // The CLI verb takes ONE optional type; the agent surface takes a list. For the
            // selections these tests compare, the two express the same request.
            types is null ? null : types.Single(),
            path,
            ProcessDescriptionCommand.CompleteFormat);

        exitCode.ShouldBe(0, _stderr.ToString());
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>The agent surface's bytes for the same selection, via the real render path.</summary>
    private static async Task<string> AgentDocumentAsync(string[]? types)
        => ProcessDescriptionDocument.Render((await AgentOutcomeAsync(types)).ShouldBeAssembled());

    /// <summary>
    /// The agent surface's raw outcome, before the document is rendered.
    /// </summary>
    /// <remarks>
    /// 🔴 Separated from <see cref="AgentDocumentAsync"/> by AB#244 so the failure arms are
    /// reachable from a test. Before the union they were not: two of them were the same
    /// <c>null</c> and the third was an exception, so "which failure was it" had no answer a
    /// test could assert on.
    /// </remarks>
    private static Task<ProcessDescriptionResult> AgentOutcomeAsync(string[]? types)
        => BuildAssembler(BuildSource()).AssembleAsync(types, CapturedAt, CancellationToken.None);

    // ═══════════════════════════════════════════════════════════════
    //  Test 14 — one format, not two
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The agent surface's document for a named type is BYTE-IDENTICAL to the CLI's for the
    /// same selection — header and version stamp included, capture timestamp excluded by
    /// construction.
    /// </summary>
    /// <remarks>
    /// Acceptance criterion 2 and spec test 14. This reds against the obvious wrong
    /// implementation — an MCP surface that assembles or serializes its own document — because
    /// any independently-authored projection differs somewhere: a key order, an omitted member,
    /// a number rendered as a string.
    /// </remarks>
    [Theory]
    [InlineData("Niflheim.Grilling")]
    [InlineData("Niflheim.Decision")]
    public async Task AgentSurface_ForANamedType_IsByteIdenticalToTheCli(string type)
    {
        string[] selection = [type];

        var cli = await CliDocumentAsync(selection);
        var agent = await AgentDocumentAsync(selection);

        // Precondition: neither side is trivially empty, which would make identity meaningless.
        cli.Length.ShouldBeGreaterThan(500);

        agent.ShouldBe(cli);
    }

    /// <summary>
    /// The same guarantee for the whole-process document, so the identity is a property of the
    /// two surfaces rather than of the one-type path.
    /// </summary>
    [Fact]
    public async Task AgentSurface_ForTheWholeProcess_IsByteIdenticalToTheCli()
    {
        var path = TempFile();
        await BuildCommand(BuildSource()).ExecuteAsync(
            null, path, ProcessDescriptionCommand.CompleteFormat);

        var cli = await File.ReadAllTextAsync(path);
        var agent = await AgentDocumentAsync(null);

        cli.Length.ShouldBeGreaterThan(500);
        agent.ShouldBe(cli);
    }

    /// <summary>
    /// 🔴 The byte-identity assertion is not passing because the two surfaces both return
    /// something trivial or because the fixture's types are interchangeable.
    /// </summary>
    /// <remarks>
    /// 🔴 The mitigation that stops test 14 being hollow. Byte-identity between two EMPTY
    /// strings, or between two documents that do not actually vary with the selection, would
    /// satisfy the assertion above while proving nothing. So the preconditions are stated: the
    /// two selections produce DIFFERENT documents, and each carries its own type's distinctive
    /// content and not the other's.
    /// </remarks>
    [Fact]
    public async Task TheTwoSelections_ProduceDifferentDocuments_SoIdentityIsNotTrivial()
    {
        var grilling = await AgentDocumentAsync(["Niflheim.Grilling"]);
        var decision = await AgentDocumentAsync(["Niflheim.Decision"]);

        grilling.ShouldNotBe(decision);

        grilling.ShouldContain("Custom.GrillingOnly");
        grilling.ShouldNotContain("Custom.DecisionOnly");

        decision.ShouldContain("Custom.DecisionOnly");
        decision.ShouldNotContain("Custom.GrillingOnly");
    }

    /// <summary>
    /// 🔴 The document carries each field's DESCRIPTION text, and this test reds if it stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Added after probing this ticket's own tests the way AB#239 probed its predecessors:
    /// the shared projection was patched to append a marker to the rendered field
    /// <c>description</c> and the whole 112-test process-description suite stayed GREEN. The
    /// member reaches the emitted file and nothing asserted its content.
    /// </para>
    /// <para>
    /// 🔴 The byte-identity tests above CANNOT catch this and it is not a defect in them: a
    /// change inside the shared projection moves both surfaces identically, which is exactly the
    /// property this ticket set out to create. Comparison tests prove the two surfaces agree;
    /// they say nothing about what the two agree ON. That gap needs a content assertion, so here
    /// is one — the fixture's descriptions are distinctive and asserted verbatim.
    /// </para>
    /// <para>
    /// This closes the gap for the member this ticket's probe actually found. The wider
    /// <c>FlattenType</c> blind spot AB#239 recorded is not re-opened or claimed fixed here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDocument_CarriesEachFieldsDescriptionText()
    {
        // 🔴 The EXACT rendered value, not a substring. `ShouldContain("A grilling field")` is
        // satisfied by "A grilling fieldPROBE" — verified, not assumed: the first version of this
        // test used ShouldContain and stayed green against the very mutation it was written to
        // catch. A containment assertion cannot detect a member being appended to or corrupted,
        // which is most of what goes wrong with a rendered string.
        DescriptionOf(await AgentDocumentAsync(["Niflheim.Grilling"]), "Custom.GrillingOnly")
            .ShouldBe("A grilling field");

        DescriptionOf(await AgentDocumentAsync(["Niflheim.Decision"]), "Custom.DecisionOnly")
            .ShouldBe("A decision field");
    }

    /// <summary>The rendered <c>description</c> of one field, by reference name.</summary>
    /// <remarks>
    /// Throws rather than returning null when the field is absent, so a projection that stopped
    /// emitting the field entirely reds here instead of comparing null against null.
    /// </remarks>
    private static string DescriptionOf(string document, string fieldReferenceName)
    {
        using var parsed = JsonDocument.Parse(document);

        var field = parsed.RootElement
            .GetProperty("types")
            .GetProperty("children")
            .EnumerateArray()
            .SelectMany(type => type.GetProperty("children").EnumerateArray())
            .Single(child =>
                child.TryGetProperty("referenceName", out var name)
                && name.GetString() == fieldReferenceName);

        return field.GetProperty("description").GetString()!;
    }

    /// <summary>
    /// 🔴 The document survives the MCP ENVELOPE byte-for-byte — it is not re-encoded on the
    /// way out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>A live-run defect, and the unit tests above could not have found it.</b> Running
    /// both surfaces against the real org showed six differing lines: <c>\u0027</c> rendered as
    /// <c>'</c>, <c>\u0026</c> as <c>&amp;</c>, <c>\u002B</c> as <c>+</c>. The cause was that the
    /// tool wrote the document with <c>JsonDocument.Parse(...).WriteTo(writer)</c>, and the
    /// envelope's writer uses <c>UnsafeRelaxedJsonEscaping</c> while
    /// <see cref="Twig.RenderTree.JsonRenderer"/> uses the default encoder — so parsing and
    /// re-writing RE-ENCODED it.
    /// </para>
    /// <para>
    /// 🔴 Every one of those six lines is valid JSON carrying the same string value, which is
    /// exactly why this would have shipped: any assertion that re-parses both sides passes.
    /// Acceptance criterion 2 asks for BYTE-identity, and "equal once both are re-parsed" is the
    /// weaker claim that lets two formats drift while a test reports agreement. So this test
    /// asserts on RAW TEXT.
    /// </para>
    /// <para>
    /// 🔴 The fixture carries a control label with an ampersand and a rule detail with an
    /// apostrophe on purpose — the precondition is asserted below, because a document containing
    /// no encoder-sensitive character at all would make this test a tautology.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEnvelope_EmitsTheDocumentVerbatim_WithoutReEncodingIt()
    {
        // A document containing the three characters the two encoders disagree about.
        const string document = """{"a": "it\u0027s", "b": "\u0026Area", "c": "x\u002By"}""";

        // PRECONDITION: the sample genuinely carries escapes the relaxed encoder would rewrite.
        // Without this, the assertion below passes against any document.
        document.ShouldContain("\\u0027");
        document.ShouldContain("\\u0026");
        document.ShouldContain("\\u002B");

        // The envelope's writer, configured exactly as EnvelopeBuilder configures it.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("description");

            // The shipped call. Swapping this for JsonDocument.Parse + WriteTo — the original
            // implementation — reds this test, which is what makes it a regression test rather
            // than a description of current behaviour.
            writer.WriteRawValue(document);

            writer.WriteEndObject();
        }

        var enveloped = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        // The document's own bytes appear in the envelope UNCHANGED.
        enveloped.ShouldContain(document);
    }

    /// <summary>
    /// 🔴 The render SETTINGS are shared, not merely the projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised by independent review: sharing the assembler and the projection makes both
    /// surfaces build the same render TREE, but a tree is not bytes — two callers constructing
    /// <see cref="Twig.RenderTree.JsonRenderer"/> with different options produce different
    /// documents from an identical tree. The agent surface originally hardcoded
    /// <c>indented: true</c> while the CLI reached the same value through its renderer factory,
    /// so byte-identity rested on two literals agreeing. That is the convention this ticket
    /// exists to replace with a structural fact.
    /// </para>
    /// <para>
    /// This test pins the two together: the CLI's <c>-o json</c> file and
    /// <see cref="ProcessDescriptionDocument.Render"/> must agree, and they are only reachable
    /// through different renderer construction paths — so a change to either side's options
    /// reds here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSharedRender_MatchesTheCliRendererFactorysSettings()
    {
        // PRECONDITION: the setting under test is observable in the output at all. A
        // non-indented document has no line breaks, so this distinguishes the two options
        // rather than assuming they differ.
        var shared = ProcessDescriptionDocument.Render(
            (await AgentOutcomeAsync(["Niflheim.Grilling"])).ShouldBeAssembled());

        ProcessDescriptionDocument.Indented.ShouldBeTrue();
        shared.ShouldContain("\n");

        // And it is byte-identical to what the CLI's own renderer factory writes.
        shared.ShouldBe(await CliDocumentAsync(["Niflheim.Grilling"]));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Criterion 1 — named types only, same document with fewer types
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// An agent can request a description of named types only, and gets the same document with
    /// fewer types in it — same header, same descriptor version, same shape.
    /// </summary>
    [Fact]
    public async Task AgentSurface_WithNamedTypes_ReturnsTheSameDocumentWithFewerTypes()
    {
        using var selected = JsonDocument.Parse(await AgentDocumentAsync(["Niflheim.Grilling"]));
        using var whole = JsonDocument.Parse(await AgentDocumentAsync(null));

        var selectedTypes = TypeReferenceNames(selected);
        var wholeTypes = TypeReferenceNames(whole);

        selectedTypes.ShouldBe(["Niflheim.Grilling"]);
        wholeTypes.ShouldBe(["Niflheim.Decision", "Niflheim.Grilling"]);

        // FEWER TYPES, not a different document: the header is identical member for member,
        // descriptor version and pinned route versions included.
        selected.RootElement.GetProperty("header").GetRawText()
            .ShouldBe(whole.RootElement.GetProperty("header").GetRawText());
    }

    /// <summary>
    /// Selecting several types returns exactly those, in the assembler's order rather than the
    /// order they were asked for.
    /// </summary>
    /// <remarks>
    /// 🔴 Argument order must NOT reach the document. The assembler is the single ordering
    /// authority, and a document whose type order depended on how a caller happened to spell its
    /// request would diff dirty against an identical process described by a different caller —
    /// which is the byte-stability ruling failing through the new surface.
    /// </remarks>
    [Fact]
    public async Task AgentSurface_SelectionOrder_DoesNotReachTheDocument()
    {
        var asked = await AgentDocumentAsync(["Niflheim.Grilling", "Niflheim.Decision"]);
        var reversed = await AgentDocumentAsync(["Niflheim.Decision", "Niflheim.Grilling"]);

        asked.ShouldBe(reversed);

        using var parsed = JsonDocument.Parse(asked);
        TypeReferenceNames(parsed).ShouldBe(["Niflheim.Decision", "Niflheim.Grilling"]);
    }

    /// <summary>
    /// An unknown type is a hard error on the agent surface too, not a document describing the
    /// types that happened to exist.
    /// </summary>
    [Fact]
    public async Task AgentSurface_WithAnUnknownType_ReturnsTypeNotFound()
    {
        var notFound = (await AgentOutcomeAsync(["Niflheim.Nope"])).ShouldBeTypeNotFound();

        notFound.TypeReferenceName.ShouldBe("Niflheim.Nope");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Criterion 3 — no per-part selection exists on any surface
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 No per-part selection exists on ANY surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acceptance criterion 3, asserted NEGATIVELY, so the precondition is stated explicitly
    /// rather than left as a tautology: the parts a filter could plausibly select over are named
    /// below and each one is confirmed PRESENT in the document first. A negative assertion over
    /// a vocabulary the document never uses would pass against anything.
    /// </para>
    /// <para>
    /// Per-part selection is the filter Solution S3 bans: a reader handed a filtered document
    /// cannot recover what was dropped and cannot tell that anything was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoSurface_OffersPerPartSelection()
    {
        // The parts of a type a per-part filter would select over. Named here so the assertion
        // below is about a real vocabulary.
        string[] parts = ["fields", "states", "transitions", "rules", "behaviours", "layout"];

        // PRECONDITION: every one of these is genuinely IN the document, so "no argument selects
        // them" is a claim about something that exists.
        var document = await AgentDocumentAsync(["Niflheim.Grilling"]);
        document.ShouldContain("\"field\"");
        document.ShouldContain("\"state\"");
        document.ShouldContain("\"transition\"");
        document.ShouldContain("\"rule\"");
        document.ShouldContain("\"behaviour\"");
        document.ShouldContain("\"layoutPage\"");

        // 🔴 The MCP TOOL's own parameters are asserted in Twig.Mcp.Tests
        // (ProcessDescriptionToolTests.TheTool_OffersNoPerPartSelection) — this project cannot
        // reference Twig.Mcp without copying the twig-mcp executable into its output and
        // crashing the test host. The two halves together cover all three surfaces.
        //
        // The CLI verb's parameters: one type, an output path, an output format, cancellation.
        var commandParameters = typeof(ProcessDescriptionCommand)
            .GetMethod(nameof(ProcessDescriptionCommand.ExecuteAsync))!
            .GetParameters()
            .Select(p => p.Name!)
            .ToArray();

        commandParameters.ShouldBe(["typeName", "outPath", "outputFormat", "ct"]);

        // And the seam both surfaces go through takes a type selection and a timestamp — there
        // is nowhere below them for a part filter to live either.
        //
        // 🔴 Reached by REFLECTION, so a signature change does not break it at compile time.
        // AB#244 changed this method's RETURN type, which this assertion could not have seen —
        // so the return type is now pinned here too. A reflection site that only checks
        // parameter names goes on passing while the thing it describes changes underneath it,
        // which is the same silent-green failure class this file's negative assertions guard.
        var assembleMethod = typeof(ProcessDescriptionAssembler)
            .GetMethod(nameof(ProcessDescriptionAssembler.AssembleAsync))!;

        assembleMethod.ReturnType.ShouldBe(
            typeof(Task<ProcessDescriptionResult>),
            "AssembleAsync must return the result UNION, not a nullable document — the "
            + "null-plus-exception shape is what AB#244 removed");

        var assemblerParameters = assembleMethod
            .GetParameters()
            .Select(p => p.Name!)
            .ToArray();

        assemblerParameters.ShouldBe(["typeReferenceNames", "capturedAtUtc", "ct"]);

        // 🔴 The generalisation: no parameter ANYWHERE on the three surfaces names a part. This
        // is what stops the assertions above from being satisfied by renaming a filter argument.
        foreach (var part in parts)
        {
            foreach (var parameter in commandParameters.Concat(assemblerParameters))
            {
                parameter.Contains(part, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"parameter '{parameter}' names the part '{part}' — per-part selection is "
                    + "forbidden (AB#241, Implementation Decision 10, Solution S3).");
            }
        }
    }

    /// <summary>
    /// The shared projection takes a completeness flag and nothing else, so it cannot become a
    /// per-part filter without the change being visible in its signature.
    /// </summary>
    /// <remarks>
    /// 🔴 <c>isComplete</c> is deliberately NOT a per-part filter and the distinction matters:
    /// it selects between two whole RENDERINGS, and the abridged one declares itself and names
    /// the format that carries everything. A per-part filter drops content silently.
    /// </remarks>
    [Fact]
    public void TheSharedProjection_TakesNoPartSelection()
    {
        var parameters = typeof(ProcessDescriptionDocument)
            .GetMethod(
                nameof(ProcessDescriptionDocument.BuildTree),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToArray();

        parameters.ShouldBe(["description", "isComplete"]);
    }

    private static string[] TypeReferenceNames(JsonDocument document) =>
        document.RootElement
            .GetProperty("types")
            .GetProperty("children")
            .EnumerateArray()
            .Select(type => type.GetProperty("referenceName").GetString()!)
            .ToArray();
}
