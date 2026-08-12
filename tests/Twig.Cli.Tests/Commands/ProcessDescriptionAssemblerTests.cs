using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// A scripted <see cref="IProcessDescriptionSource"/> that lets a test drive the ORDER in
/// which per-type detail fetches complete.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 This exists because byte-stability under concurrency is the property most likely to
/// break and the hardest to test honestly. Parallel fetching is a ruled mitigation, not an
/// optimisation, so it cannot simply be removed — and asserting it by wall-clock timing
/// would be a flaky test that proves nothing.
/// </para>
/// <para>
/// So completion order is driven explicitly instead. <see cref="CompletionOrder"/> names the
/// types in the order their detail tasks are allowed to finish; every call blocks on a
/// <see cref="TaskCompletionSource"/> until its turn arrives. A test can therefore run the
/// same fetches in natural order and in EXACTLY REVERSED order and assert the two documents
/// are byte-identical — which is a real property, not a timing coincidence.
/// </para>
/// </remarks>
internal sealed class ScriptedDescriptionSource : IProcessDescriptionSource
{
    private readonly Dictionary<string, ProcessTypeDetail> _details;
    private readonly List<string> _typeOrder;
    private readonly Dictionary<string, TaskCompletionSource> _gates;

    /// <summary>Types in the order their detail fetches are released to complete.</summary>
    public IReadOnlyList<string>? CompletionOrder { get; init; }

    /// <summary>Every reference name whose detail was requested, in call order.</summary>
    public List<string> RequestedTypes { get; } = [];

    /// <summary>
    /// The <c>inheritsFrom</c> argument each detail fetch was given, keyed by type.
    /// </summary>
    /// <remarks>
    /// Recorded so a test can prove the parent reference name actually REACHES the fetch
    /// layer. It is what lets the transitions route find a derived type, and dropping it
    /// produces a document that is silently wrong (zero transitions) rather than one that
    /// fails.
    /// </remarks>
    public Dictionary<string, string?> RequestedInherits { get; } = [];

    /// <summary>How many times the identity/type-list routes were hit — proves no caching.</summary>
    public int IdentityCallCount { get; private set; }
    public int TypeListCallCount { get; private set; }

    public ProcessIdentity? Identity { get; init; } = new(
        "https://dev.azure.com/ExampleOrg",
        "Twig",
        // 🔴 A process ID, deliberately not a name. The live trap this design answers: the
        // project named "Twig" does not run on the process named "Twig".
        "7f984e4c-e856-4fc3-8457-fd4e8acf2e57",
        "Niflheim");

    public ScriptedDescriptionSource(
        IReadOnlyList<string> typeOrder,
        Dictionary<string, ProcessTypeDetail> details)
    {
        _typeOrder = [.. typeOrder];
        _details = details;
        _gates = typeOrder.ToDictionary(t => t, _ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public Task<ProcessIdentity?> GetProcessIdentityAsync(CancellationToken ct = default)
    {
        IdentityCallCount++;
        return Task.FromResult(Identity);
    }

    public Task<IReadOnlyList<ProcessTypeSummary>?> GetTypesAsync(CancellationToken ct = default)
    {
        TypeListCallCount++;

        IReadOnlyList<ProcessTypeSummary>? summaries = [.. _typeOrder.Select(reference =>
            new ProcessTypeSummary(
                reference,
                // 🔴 Display names deliberately COLLIDE across two types here. Display names
                // lie; if anything in the pipeline matched on them the document would fuse or
                // mis-order these two, and the byte-stability tests would still pass.
                "Shared Display Name",
                $"description of {reference}",
                reference.Contains("Custom", StringComparison.Ordinal) ? "custom" : "inherited",
                reference.Contains("Custom", StringComparison.Ordinal) ? null : "Microsoft.VSTS.WorkItemTypes.Task",
                IsDisabled: false))];

        return Task.FromResult<IReadOnlyList<ProcessTypeSummary>?>(summaries);
    }

    public async Task<ProcessTypeDetail?> GetTypeDetailAsync(
        string typeReferenceName,
        string? inheritsFrom = null,
        CancellationToken ct = default)
    {
        lock (RequestedTypes)
        {
            RequestedTypes.Add(typeReferenceName);
            RequestedInherits[typeReferenceName] = inheritsFrom;
        }

        if (CompletionOrder is not null)
        {
            // Release this fetch only when the script says it is this type's turn. Every
            // earlier gate must already be signalled, so completion order is exact.
            await _gates[typeReferenceName].Task;
        }

        return _details.TryGetValue(typeReferenceName, out var detail) ? detail : null;
    }

    /// <summary>
    /// Releases the detail fetches in <see cref="CompletionOrder"/>, one at a time.
    /// </summary>
    /// <remarks>
    /// Called on a background task by the test so the assembler's <c>Task.WhenAll</c> can be
    /// in flight while the gates open. Each gate is opened only after the previous one's
    /// continuation has had a chance to run, which is what makes "reversed" mean reversed.
    /// </remarks>
    public async Task ReleaseInScriptedOrderAsync()
    {
        foreach (var type in CompletionOrder ?? [])
        {
            _gates[type].SetResult();
            await Task.Yield();
        }
    }
}

/// <summary>
/// The byte-stability suite: the single most important behaviour this ticket ships.
/// </summary>
/// <remarks>
/// 🔴 Byte-stability is a hard requirement, not a quality goal. Two runs against an unchanged
/// process must produce byte-identical documents, the header's capture timestamp excepted —
/// and that is the ONLY permitted variance. If ordering wobbles, the diff a reader runs fills
/// with noise and the whole feature is worthless.
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Solution S2, tests 1 and 2
/// of its table.
/// </para>
/// </remarks>
public sealed class ProcessDescriptionAssemblerTests
{
    private static readonly DateTimeOffset FixedCapture =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Detail whose collections are deliberately in a HOSTILE order — reverse-sorted fields,
    /// out-of-order states, scrambled transitions.
    /// </summary>
    /// <remarks>
    /// 🔴 The fixture's precondition is asserted in the tests that depend on it. A fixture
    /// that happened to arrive pre-sorted would let an assembler that does no sorting at all
    /// pass every ordering test — the same hollow-guard class this repo has been bitten by.
    /// </remarks>
    private static ProcessTypeDetail HostileDetail(string seed) => new(
        Fields:
        [
            new ProcessTypeField($"Custom.{seed}Zeta", "Zeta", "string", null, false, "custom", false, ""),
            new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, ""),
            new ProcessTypeField($"Custom.{seed}Alpha", "Alpha", "string", "d", false, "custom", false, ""),
        ],
        States:
        [
            new ProcessTypeState("Done", "Completed", 3, "339947", "custom", false),
            new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false),
            new ProcessTypeState("Doing", "InProgress", 2, "007acc", "custom", false),
        ],
        Transitions:
        [
            new ProcessTypeTransition("Done", "To do"),
            new ProcessTypeTransition("", "To do"),
            new ProcessTypeTransition("To do", "Doing"),
        ]);

    private static ScriptedDescriptionSource BuildSource(
        IReadOnlyList<string>? completionOrder = null,
        IReadOnlyList<string>? typeOrder = null)
    {
        // Reference names deliberately NOT in sorted order, so a document that emitted them
        // in arrival order would differ from one that sorted.
        var types = typeOrder ?? ["Niflheim.CustomZulu", "Microsoft.VSTS.WorkItemTypes.Task", "Niflheim.CustomAlpha"];

        return new ScriptedDescriptionSource(
            types,
            types.ToDictionary(t => t, HostileDetail))
        {
            CompletionOrder = completionOrder,
        };
    }

    private static ProcessDescriptionAssembler BuildAssembler(IProcessDescriptionSource source) =>
        new(source)
        {
            RouteVersions =
            [
                new ProcessDescriptionRouteVersion("work/processes/{processId}/workItemTypes", "7.1-preview.2"),
                new ProcessDescriptionRouteVersion("core/projects/{project}", "7.1"),
            ],
        };

    /// <summary>
    /// A stable projection of the whole document, used to compare two assemblies for
    /// byte-identity.
    /// </summary>
    /// <remarks>
    /// Deliberately serialises EVERY ordered position of every collection, including the
    /// header's route list and known gaps. A projection that only compared counts, or only
    /// the type list, would pass against an implementation whose FIELD order wobbled — which
    /// is precisely the failure mode being defended against.
    /// </remarks>
    private static string Flatten(ProcessDescription description)
    {
        var writer = new StringWriter();
        var header = description.Header;

        writer.WriteLine($"org={header.Organization}");
        writer.WriteLine($"project={header.ProjectName}");
        writer.WriteLine($"processId={header.ProcessId}");
        writer.WriteLine($"processName={header.ProcessName}");
        writer.WriteLine($"descriptorVersion={header.DescriptorVersion}");
        foreach (var route in header.RouteApiVersions)
            writer.WriteLine($"route={route.Route}@{route.ApiVersion}");
        foreach (var gap in header.KnownGaps)
            writer.WriteLine($"gap={gap.Subject}|{gap.TrackedIn}");

        foreach (var type in description.Types)
        {
            writer.WriteLine($"type={type.ReferenceName}|{type.Name}|{type.Customization}|{type.Inherits}");
            foreach (var field in type.Fields)
                writer.WriteLine(
                    $"  field={field.ReferenceName}|{field.Name}|{field.Type}|"
                    + $"{field.RequiredUnconditionally}|{field.DefaultValue}|"
                    + $"{field.Customization}|{field.Description}");
            foreach (var state in type.States)
                writer.WriteLine($"  state={state.Name}|{state.StateCategory}|{state.Order}");
            foreach (var transition in type.Transitions)
                writer.WriteLine($"  transition={transition.FromState}->{transition.ToState}");
            foreach (var unfetched in type.Unfetched)
                writer.WriteLine($"  unfetched={unfetched}");
        }

        return writer.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 1 — byte-stability
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 THE test. Two runs against an unchanged process produce an identical document, with
    /// the capture timestamp excluded by construction (it is injected, so the two runs are
    /// given the same instant and every OTHER difference would show).
    /// </summary>
    [Fact]
    public async Task Assemble_TwoRunsAgainstUnchangedProcess_ProduceIdenticalDocuments()
    {
        var first = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);
        var second = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        Flatten(first).ShouldBe(Flatten(second));
    }

    /// <summary>
    /// The timestamp is the ONLY permitted variance. Two runs at different instants differ in
    /// the header and nowhere else.
    /// </summary>
    [Fact]
    public async Task Assemble_AtDifferentInstants_DiffersOnlyInTheCaptureTimestamp()
    {
        var later = FixedCapture.AddHours(9);

        var first = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);
        var second = await BuildAssembler(BuildSource()).AssembleAsync(null, later);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();

        // Precondition: the two really were taken at different instants, or this test is a
        // restatement of the one above.
        first.Header.CapturedAtUtc.ShouldNotBe(second.Header.CapturedAtUtc);
        Flatten(first).ShouldBe(Flatten(second));
    }

    /// <summary>
    /// Ordering is imposed by the assembler, not inherited from the wire. Asserts the actual
    /// sorted order rather than merely that two runs agree — two runs of an unsorted
    /// assembler fed the same fixture would also agree.
    /// </summary>
    [Fact]
    public async Task Assemble_SortsTypesFieldsStatesAndTransitionsDeterministically()
    {
        var source = BuildSource();

        // Precondition: the fixture is NOT already in the order asserted below. Without this
        // an assembler that sorts nothing would pass.
        var wireOrder = (await source.GetTypesAsync())!.Select(t => t.ReferenceName).ToList();
        wireOrder.ShouldNotBe([.. wireOrder.OrderBy(x => x, StringComparer.Ordinal)]);

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);
        description.ShouldNotBeNull();

        description.Types.Select(t => t.ReferenceName)
            .ShouldBe(["Microsoft.VSTS.WorkItemTypes.Task", "Niflheim.CustomAlpha", "Niflheim.CustomZulu"]);

        var first = description.Types[0];
        first.Fields.Select(f => f.ReferenceName)
            .ShouldBe([.. first.Fields.Select(f => f.ReferenceName).OrderBy(x => x, StringComparer.Ordinal)]);
        first.States.Select(s => s.Name).ShouldBe(["To do", "Doing", "Done"]);
        // The initial transition (empty from-state) sorts first.
        first.Transitions[0].FromState.ShouldBe("");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 2 — parallel fetch does not perturb ordering
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The document is byte-identical whether the independent per-type fetches complete in
    /// order or in EXACTLY REVERSED order.
    /// </summary>
    /// <remarks>
    /// Parallelism is the ruled latency mitigation and also the thing most likely to break
    /// byte-stability. Completion order is driven explicitly at the fetch abstraction — never
    /// by wall-clock timing, which would be flaky and would prove nothing.
    /// </remarks>
    [Fact]
    public async Task Assemble_WithReversedFetchCompletionOrder_ProducesTheIdenticalDocument()
    {
        var types = new[] { "Niflheim.CustomZulu", "Microsoft.VSTS.WorkItemTypes.Task", "Niflheim.CustomAlpha" };
        var reversed = types.Reverse().ToArray();

        // Precondition: the two scripts really are different orders.
        reversed.ShouldNotBe(types);

        var inOrderSource = BuildSource(completionOrder: types, typeOrder: types);
        var inOrderTask = BuildAssembler(inOrderSource).AssembleAsync(null, FixedCapture);
        await inOrderSource.ReleaseInScriptedOrderAsync();
        var inOrder = await inOrderTask;

        var reversedSource = BuildSource(completionOrder: reversed, typeOrder: types);
        var reversedTask = BuildAssembler(reversedSource).AssembleAsync(null, FixedCapture);
        await reversedSource.ReleaseInScriptedOrderAsync();
        var reversedResult = await reversedTask;

        inOrder.ShouldNotBeNull();
        reversedResult.ShouldNotBeNull();
        Flatten(inOrder).ShouldBe(Flatten(reversedResult));
    }

    /// <summary>
    /// The whole-process path issues its per-type fetches CONCURRENTLY, not one after the
    /// other.
    /// </summary>
    /// <remarks>
    /// Asserted at the fetch abstraction, per the spec: with every gate shut, all three
    /// requests must already have arrived. A serial implementation could only have issued
    /// one, because it would be blocked awaiting the first. No timing involved.
    /// </remarks>
    [Fact]
    public async Task Assemble_WholeProcess_IssuesPerTypeFetchesConcurrently()
    {
        var types = new[] { "Niflheim.CustomZulu", "Microsoft.VSTS.WorkItemTypes.Task", "Niflheim.CustomAlpha" };
        var source = BuildSource(completionOrder: types, typeOrder: types);

        var assembleTask = BuildAssembler(source).AssembleAsync(null, FixedCapture);

        // Spin until all three are in flight, bounded so a serial implementation fails the
        // test rather than hanging it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (source.RequestedTypes.Count < types.Length && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        source.RequestedTypes.Count.ShouldBe(
            types.Length,
            "all per-type fetches must be in flight together — a serial implementation would "
            + "have issued only the first while awaiting it");

        await source.ReleaseInScriptedOrderAsync();
        (await assembleTask).ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Identity, selection, and the honest incompleteness
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The process is identified by ID, and the document records it. A name collision
    /// cannot make this describe the wrong process because no name is ever resolved on.
    /// </summary>
    [Fact]
    public async Task Assemble_RecordsTheProcessIdNotJustTheName()
    {
        var description = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);

        description.ShouldNotBeNull();
        description.Header.ProcessId.ShouldBe("7f984e4c-e856-4fc3-8457-fd4e8acf2e57");
        // Precondition: the id and the name are genuinely different strings, so an
        // implementation that put the name in the id slot would fail here.
        description.Header.ProcessName.ShouldNotBe(description.Header.ProcessId);
    }

    /// <summary>
    /// Types are matched by REFERENCE name. The fixture gives two types the SAME display
    /// name; selecting one reference name must return exactly that type.
    /// </summary>
    [Fact]
    public async Task Assemble_NamedType_MatchesOnReferenceNameNotDisplayName()
    {
        var source = BuildSource();

        // Precondition: display names really do collide, or this proves nothing.
        var summaries = await source.GetTypesAsync();
        summaries!.Select(s => s.Name).Distinct().Count().ShouldBe(1);

        var description = await BuildAssembler(source)
            .AssembleAsync(["Niflheim.CustomAlpha"], FixedCapture);

        description.ShouldNotBeNull();
        description.Types.Count.ShouldBe(1);
        description.Types[0].ReferenceName.ShouldBe("Niflheim.CustomAlpha");
    }

    /// <summary>
    /// A named type produces the SAME document shape as the whole-process one, with fewer
    /// types in it — same header, same version stamp, same ordering. Not a second format.
    /// </summary>
    [Fact]
    public async Task Assemble_NamedType_ProducesTheSameDocumentShapeWithFewerTypes()
    {
        var whole = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);
        var single = await BuildAssembler(BuildSource())
            .AssembleAsync(["Niflheim.CustomAlpha"], FixedCapture);

        whole.ShouldNotBeNull();
        single.ShouldNotBeNull();

        single.Types.Count.ShouldBeLessThan(whole.Types.Count);
        single.Header.DescriptorVersion.ShouldBe(whole.Header.DescriptorVersion);
        single.Header.ProcessId.ShouldBe(whole.Header.ProcessId);
        single.Header.RouteApiVersions.Select(r => r.Route)
            .ShouldBe(whole.Header.RouteApiVersions.Select(r => r.Route));

        // The type entry itself is identical between the two documents. Compared member-wise
        // rather than with record equality: a record's synthesized Equals compares the
        // collection PROPERTIES by reference, so `ShouldBe` would fail on two structurally
        // identical documents and pass on nothing useful.
        var fromWhole = whole.Types.Single(t => t.ReferenceName == "Niflheim.CustomAlpha");
        var fromSingle = single.Types[0];

        fromSingle.Name.ShouldBe(fromWhole.Name);
        fromSingle.Customization.ShouldBe(fromWhole.Customization);
        fromSingle.Inherits.ShouldBe(fromWhole.Inherits);
        fromSingle.Fields.ShouldBe(fromWhole.Fields);
        fromSingle.States.ShouldBe(fromWhole.States);
        fromSingle.Transitions.ShouldBe(fromWhole.Transitions);
    }

    /// <summary>An unknown type is a hard error naming what was asked for, not an empty document.</summary>
    [Fact]
    public async Task Assemble_UnknownType_ThrowsNamingTheTypeAskedFor()
    {
        var ex = await Should.ThrowAsync<ProcessDescriptionTypeNotFoundException>(
            () => BuildAssembler(BuildSource()).AssembleAsync(["Niflheim.NoSuchType"], FixedCapture));

        ex.TypeReferenceName.ShouldBe("Niflheim.NoSuchType");
        ex.Message.ShouldContain("Niflheim.NoSuchType");
    }

    /// <summary>
    /// The header carries org, project, process, descriptor version, and the pinned
    /// api-version per route.
    /// </summary>
    [Fact]
    public async Task Assemble_HeaderCarriesProvenanceAndPinnedRouteVersions()
    {
        var description = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);

        description.ShouldNotBeNull();
        description.Header.Organization.ShouldBe("https://dev.azure.com/ExampleOrg");
        description.Header.ProjectName.ShouldBe("Twig");
        description.Header.DescriptorVersion.ShouldBe("0.1");
        description.Header.CapturedAtUtc.ShouldBe(FixedCapture);

        // Route versions are sorted so two documents line up, and carry a real version.
        description.Header.RouteApiVersions.Count.ShouldBe(2);
        description.Header.RouteApiVersions.Select(r => r.Route)
            .ShouldBe([.. description.Header.RouteApiVersions.Select(r => r.Route).OrderBy(x => x, StringComparer.Ordinal)]);
        description.Header.RouteApiVersions
            .ShouldContain(r => r.Route.EndsWith("workItemTypes", StringComparison.Ordinal)
                && r.ApiVersion == "7.1-preview.2");
    }

    /// <summary>
    /// 🔴 The document declares what it is NOT yet trustworthy about. At 0.1 that is
    /// conditional requiredness (AB#236) and picklist values (AB#237).
    /// </summary>
    /// <remarks>
    /// This is the ticket's "KNOWN INCOMPLETE" acceptance criterion made enforceable. Without
    /// it, a later contributor could delete the reservations and convert an honestly
    /// incomplete document into a silently wrong one with nothing failing.
    /// </remarks>
    [Fact]
    public async Task Assemble_HeaderDeclaresTheKnownGapsWithTheirTickets()
    {
        var description = await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture);

        description.ShouldNotBeNull();

        var gaps = description.Header.KnownGaps;
        gaps.ShouldContain(g => g.Subject == "conditionalRequiredness" && g.TrackedIn == "AB#236");
        gaps.ShouldContain(g => g.Subject == "picklistValues" && g.TrackedIn == "AB#237");

        // Sorted, like everything else, so the reservations do not swap between two documents.
        gaps.Select(g => g.Subject)
            .ShouldBe([.. gaps.Select(g => g.Subject).OrderBy(x => x, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 🔴 No caching of any kind: a second assembly re-fetches everything rather than serving
    /// a memoized answer. A stale description is a wrong description.
    /// </summary>
    [Fact]
    public async Task Assemble_TwiceOnTheSameAssembler_RefetchesRatherThanCaching()
    {
        var source = BuildSource();
        var assembler = BuildAssembler(source);

        await assembler.AssembleAsync(null, FixedCapture);
        var afterFirst = source.TypeListCallCount;

        await assembler.AssembleAsync(null, FixedCapture);

        afterFirst.ShouldBeGreaterThan(0);
        source.TypeListCallCount.ShouldBeGreaterThan(
            afterFirst,
            "the second run must re-fetch — a cache would trade away the one property the "
            + "artifact exists to have");
    }

    /// <summary>
    /// The assembler carries structural field metadata through without inventing or dropping
    /// any of it.
    /// </summary>
    /// <remarks>
    /// 🔴 The "no work item values" guard lives at the COMMAND level
    /// (<c>Execute_RenderedDocumentContainsNoWorkItemValues</c>), asserted against the real
    /// rendered document. It used to live here, searching this class's <c>Flatten()</c>
    /// helper — which did not emit field descriptions, so a value planted in one would have
    /// reached the actual document while the test passed. A negative assertion is only worth
    /// anything when it is pointed at the surface the value would actually reach.
    /// </remarks>
    [Fact]
    public async Task Assemble_CarriesStructuralFieldMetadataVerbatim()
    {
        var types = new[] { "Niflheim.CustomAlpha" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.CustomAlpha"] = new(
                    Fields:
                    [
                        new ProcessTypeField(
                            "System.Title", "Title", "string", "a default", true, "system", false,
                            Description: "the field's own description"),
                    ],
                    States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                    Transitions: [new ProcessTypeTransition("", "To do")]),
            });

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);
        description.ShouldNotBeNull();

        var field = description.Types[0].Fields.ShouldHaveSingleItem();
        field.ReferenceName.ShouldBe("System.Title");
        field.DefaultValue.ShouldBe("a default");
        field.RequiredUnconditionally.ShouldBeTrue();
        field.Customization.ShouldBe("system");
        field.Description.ShouldBe("the field's own description");
    }

    /// <summary>
    /// A type whose detail could not be fetched stays in the document with empty collections
    /// rather than disappearing.
    /// </summary>
    /// <remarks>
    /// Dropping it would read as "this process does not have this type", which is a different
    /// and wrong claim — and in a comparison document, a type's presence or absence is exactly
    /// the difference the reader is looking for.
    /// </remarks>
    [Fact]
    public async Task Assemble_TypeWithUnfetchableDetail_StaysInTheDocumentWithEmptyCollections()
    {
        var types = new[] { "Niflheim.CustomAlpha", "Niflheim.CustomZulu" };
        var source = new ScriptedDescriptionSource(
            types,
            // Zulu deliberately absent → its detail fetch returns null.
            new Dictionary<string, ProcessTypeDetail> { ["Niflheim.CustomAlpha"] = HostileDetail("a") });

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        description.ShouldNotBeNull();
        description.Types.Select(t => t.ReferenceName)
            .ShouldBe(["Niflheim.CustomAlpha", "Niflheim.CustomZulu"]);

        var unfetchable = description.Types.Single(t => t.ReferenceName == "Niflheim.CustomZulu");
        unfetchable.Fields.ShouldBeEmpty();
        unfetchable.States.ShouldBeEmpty();
    }

    /// <summary>
    /// 🔴 A DERIVED type's parent reference name reaches the fetch layer.
    /// </summary>
    /// <remarks>
    /// Regression test for a defect found against the LIVE API during this build. The two
    /// routes disagree on a derived type's name — it is <c>Niflheim.Epic</c> on the process
    /// routes and <c>Microsoft.VSTS.WorkItemTypes.Epic</c> on the project-scoped route that
    /// is the only source of transitions. The first implementation passed only the process
    /// name, so all three derived types in the real process came back with ZERO transitions.
    /// <para>
    /// That is the worst available failure shape: "no transitions" is a plausible answer, so
    /// nothing failed and the document was quietly wrong. This test asserts the parent name
    /// is threaded through, which is what makes the fallback possible at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_PassesADerivedTypesParentReferenceNameToTheFetchLayer()
    {
        var source = BuildSource();

        // Precondition: the fixture really does contain both a derived type (which HAS a
        // parent) and an authored one (which does not). Without both, this could pass
        // against an implementation that always passed null.
        var summaries = await source.GetTypesAsync();
        summaries.ShouldNotBeNull();
        summaries.Any(s => s.Inherits != null).ShouldBeTrue();
        summaries.Any(s => s.Inherits == null).ShouldBeTrue();

        await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        source.RequestedInherits["Microsoft.VSTS.WorkItemTypes.Task"]
            .ShouldBe(
                "Microsoft.VSTS.WorkItemTypes.Task",
                "a derived type's parent reference name must reach the fetch layer, or the "
                + "transitions route cannot find it and silently reports none");

        // An authored type has no parent, and must not be given a fabricated one.
        source.RequestedInherits["Niflheim.CustomAlpha"].ShouldBeNull();
    }

    /// <summary>
    /// 🔴 A type whose detail could not be fetched is DISTINGUISHABLE from one that genuinely
    /// has nothing.
    /// </summary>
    /// <remarks>
    /// Found by independent review. Previously both rendered as empty collections, so "we
    /// failed to ask" and "this type has no fields" were byte-identical in the document —
    /// wrong in the silent direction, and exactly the failure class this feature exists to
    /// prevent. On this route family a 404 is count-shaped and looks like thin success, which
    /// makes the confusion easy to reach.
    /// </remarks>
    [Fact]
    public async Task Assemble_TypeWithUnfetchableDetail_IsDistinguishableFromAGenuinelyEmptyType()
    {
        var types = new[] { "Niflheim.CustomAlpha", "Niflheim.CustomZulu" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                // Genuinely empty — the fetch SUCCEEDED and the type really has nothing.
                ["Niflheim.CustomAlpha"] = new([], [], []),
                // Zulu absent → its detail fetch returns null, i.e. it could not be read.
            });

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);
        description.ShouldNotBeNull();

        var genuinelyEmpty = description.Types.Single(t => t.ReferenceName == "Niflheim.CustomAlpha");
        var couldNotRead = description.Types.Single(t => t.ReferenceName == "Niflheim.CustomZulu");

        // Precondition: both carry empty collections, so the ONLY thing that can tell them
        // apart is the explicit label. Without this check the test could pass on a difference
        // that is not the one under test.
        genuinelyEmpty.Fields.ShouldBeEmpty();
        couldNotRead.Fields.ShouldBeEmpty();

        genuinelyEmpty.Unfetched.ShouldBeEmpty();
        couldNotRead.Unfetched.ShouldBe(["fields", "states", "transitions"]);
    }

    /// <summary>
    /// 🔴 A PARTIAL fetch failure is named, not swallowed.
    /// </summary>
    /// <remarks>
    /// The subtler half of the same defect: when the fields call fails but states and
    /// transitions succeed, the type looks fully described apart from having no fields — a
    /// completely plausible-looking document that understates the process.
    /// </remarks>
    [Fact]
    public async Task Assemble_TypeWithPartiallyUnfetchableDetail_NamesOnlyTheFailedParts()
    {
        var types = new[] { "Niflheim.CustomAlpha" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.CustomAlpha"] = new(
                    Fields: [],
                    States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                    Transitions: [new ProcessTypeTransition("", "To do")],
                    Unfetched: ["fields"]),
            });

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);
        description.ShouldNotBeNull();

        var type = description.Types[0];
        type.Unfetched.ShouldBe(["fields"]);
        // The parts that DID arrive are still carried — a partial failure does not discard
        // the truth that was successfully read.
        type.States.Count.ShouldBe(1);
        type.Transitions.Count.ShouldBe(1);
    }

    /// <summary>
    /// The unfetched labels are ordered, so two documents cannot differ merely in the order a
    /// fetch layer happened to report its failures.
    /// </summary>
    [Fact]
    public async Task Assemble_UnfetchedLabels_AreOrdinalSortedAndDeduplicated()
    {
        var types = new[] { "Niflheim.CustomAlpha" };
        var scrambled = new[] { "transitions", "fields", "states", "fields" };
        var source = new ScriptedDescriptionSource(
            types,
            new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.CustomAlpha"] = new([], [], [], scrambled),
            });

        // Precondition: the input is genuinely unsorted and contains a duplicate.
        scrambled.ShouldNotBe([.. scrambled.OrderBy(x => x, StringComparer.Ordinal)]);

        var description = await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        description.ShouldNotBeNull();
        description.Types[0].Unfetched.ShouldBe(["fields", "states", "transitions"]);
    }

    /// <summary>
    /// 🔴 The same type named twice is described ONCE.
    /// </summary>
    /// <remarks>
    /// Found by independent review. Matching is case-INSENSITIVE, so two spellings of one
    /// reference name resolved to the same type, fetched it twice, and emitted it twice — and
    /// because the document is sorted by reference name the copies landed adjacent, reading
    /// like a genuine duplicate in the process rather than a caller artefact.
    /// </remarks>
    [Fact]
    public async Task Assemble_SameTypeNamedTwiceInDifferentCasing_IsDescribedOnce()
    {
        var source = BuildSource();

        var description = await BuildAssembler(source).AssembleAsync(
            ["Niflheim.CustomAlpha", "niflheim.customalpha", "Niflheim.CustomAlpha"],
            FixedCapture);

        description.ShouldNotBeNull();
        description.Types.Count.ShouldBe(1);
        description.Types[0].ReferenceName.ShouldBe("Niflheim.CustomAlpha");

        // And it was fetched once, not three times — the duplicate was collapsed before the
        // fan-out, not merely de-duplicated in the output.
        source.RequestedTypes.Count(t => string.Equals(
            t, "Niflheim.CustomAlpha", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    /// <summary>An unresolvable process yields no document rather than an empty one.</summary>
    [Fact]
    public async Task Assemble_WhenProcessCannotBeResolved_ReturnsNull()
    {
        var source = new ScriptedDescriptionSource([], []) { Identity = null };

        (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeNull();
    }
}
