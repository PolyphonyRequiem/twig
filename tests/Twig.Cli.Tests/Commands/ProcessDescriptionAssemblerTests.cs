using Shouldly;
using Twig.Cli.Tests.TestSupport;
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

    private int _inFlight;
    private int _maxConcurrentDetailFetches;

    /// <summary>
    /// The greatest number of per-type detail fetches that were ever in flight at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Records the HIGH-WATER MARK rather than a snapshot, because the property under test
    /// is two-sided and both sides are real (AB#239). Concurrency is a RULED mitigation, so a
    /// serialised implementation must red; but the fan-out is also deliberately BOUNDED,
    /// because each type's detail call issues five concurrent GETs and fourteen types ungated
    /// is a 429 generator — and throttling degrades exactly the answers this document exists
    /// to make trustworthy.
    /// </para>
    /// <para>
    /// A test asserting only "more than one was in flight" is blind to the bound being
    /// removed, and one asserting only the bound is blind to the fetches being serialised.
    /// This counter lets a single observation carry both.
    /// </para>
    /// <para>
    /// 🔴 READ under the same lock it is written under. It is mutated by N worker threads and
    /// read from the test thread; an unsynchronised read of a plain field has no
    /// acquire/release pairing, so the reader may observe a stale value and spin out its
    /// deadline against a CORRECT implementation. Green on x86 is not the same as correct.
    /// </para>
    /// </remarks>
    public int MaxConcurrentDetailFetches
    {
        get { lock (RequestedTypes) return _maxConcurrentDetailFetches; }
    }

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

    /// <summary>How many times the org-scoped picklist source was hit.</summary>
    public int ConstraintCallCount { get; private set; }

    /// <summary>
    /// The value constraints the picklist source reports, keyed by field reference name.
    /// </summary>
    /// <remarks>
    /// 🔴 Defaults to an EMPTY MAP, not to <c>null</c>. An empty map means "the source was
    /// read and reported nothing about these fields", which resolves every field to
    /// <c>Unknown</c>; <c>null</c> means the call FAILED and additionally puts
    /// <c>picklists</c> in each type's unfetched list. Two different facts, and a fixture that
    /// conflated them would make the unfetched-label test meaningless.
    /// </remarks>
    public IReadOnlyDictionary<string, FieldValueConstraint>? ValueConstraints { get; init; }
        = new Dictionary<string, FieldValueConstraint>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// When true, the type-list route does not answer — <see cref="GetTypesAsync"/> returns
    /// <c>null</c>, the way the real source reports a failed fetch.
    /// </summary>
    /// <remarks>
    /// 🔴 Distinct from a null <see cref="Identity"/> and that is the point (AB#244). "This
    /// project has no process" and "the type list route did not answer" are different failures
    /// with different remedies, and until this flag existed the fixture could only produce the
    /// first — so the second had no test to fail.
    /// </remarks>
    public bool TypeListUnfetchable { get; init; }

    public Task<IReadOnlyList<ProcessTypeSummary>?> GetTypesAsync(CancellationToken ct = default)
    {
        TypeListCallCount++;

        if (TypeListUnfetchable)
            return Task.FromResult<IReadOnlyList<ProcessTypeSummary>?>(null);

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

            // 🔴 Incremented INSIDE the lock, after the ASSEMBLER's semaphore has admitted
            // this call and before the fixture's own completion gate is awaited. So the
            // high-water mark counts calls that are past the gate under test and still in
            // flight — which is the quantity the bound is a bound on. (Two different gates
            // are in play; conflating them would make the counter measure arrivals rather
            // than concurrency, and the test meaningless.)
            _inFlight++;
            if (_inFlight > _maxConcurrentDetailFetches)
                _maxConcurrentDetailFetches = _inFlight;
        }

        try
        {
            if (CompletionOrder is not null)
            {
                // Release this fetch only when the script says it is this type's turn. Every
                // earlier gate must already be signalled, so completion order is exact.
                await _gates[typeReferenceName].Task;
            }

            return _details.TryGetValue(typeReferenceName, out var detail) ? detail : null;
        }
        finally
        {
            lock (RequestedTypes)
                _inFlight--;
        }
    }

    public Task<IReadOnlyDictionary<string, FieldValueConstraint>?> GetFieldValueConstraintsAsync(
        CancellationToken ct = default)
    {
        ConstraintCallCount++;
        return Task.FromResult(ValueConstraints);
    }

    /// <summary>How many times the process-scoped behaviour catalogue was hit.</summary>
    public int BehaviourCatalogueCallCount { get; private set; }

    /// <summary>
    /// The behaviour catalogue the process reports, used to name membership edges.
    /// </summary>
    /// <remarks>
    /// 🔴 Defaults to an EMPTY LIST, not <c>null</c>, for the same reason
    /// <see cref="ValueConstraints"/> does. An empty catalogue means "we read it and it names
    /// none of these behaviours", which leaves memberships present but unnamed; <c>null</c>
    /// means the call FAILED and additionally puts <c>behaviourCatalogue</c> in the unfetched
    /// list of every type that HAS memberships. Two different facts, and conflating them would
    /// make the unfetched-label test meaningless.
    /// </remarks>
    public IReadOnlyList<ProcessBehaviourSummary>? BehaviourCatalogue { get; init; } = [];

    public Task<IReadOnlyList<ProcessBehaviourSummary>?> GetBehaviourCatalogueAsync(
        CancellationToken ct = default)
    {
        BehaviourCatalogueCallCount++;
        return Task.FromResult(BehaviourCatalogue);
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
        ],
        Unfetched: null,
        // 🔴 Hostile here too (AB#238), and rules are the biggest hazard of the three: they
        // arrive in server order at ~54 per derived type. Deliberately reverse-tagged
        // (system first, custom last) with the conditions and actions of one rule ALSO out of
        // order, so an assembler that sorted rules but not their internals would still red.
        Rules:
        [
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("whenWas", "System.State", "Doing"),
                    new RuleCondition("when", "System.State", "Done"),
                ],
                Actions:
                [
                    new RuleAction("makeReadOnly", "System.Reason", null),
                    new RuleAction("copyValue", "System.Reason", "Completed"),
                ],
                IsDisabled: false,
                Customization: RuleCustomization.From("system")),
            new ProcessRule(
                Conditions: [new RuleCondition("when", "System.State", "Done")],
                Actions: [new RuleAction("makeRequired", $"Custom.{seed}Alpha", null)],
                IsDisabled: false,
                Customization: RuleCustomization.From("custom"),
                Name: "Authored rule"),
        ],
        // Reverse-sorted by reference name, so a document emitting arrival order differs.
        Behaviours:
        [
            new ProcessBehaviourMembership("Custom.Zeta", string.Empty, null, false),
            new ProcessBehaviourMembership("Custom.Alpha", string.Empty, null, true),
        ],
        // 🔴 Pages, groups and controls all presented with their `order` keys DESCENDING, so
        // an assembler that trusted array order rather than sorting on the key would emit the
        // form upside down — and the byte-stability comparison would still pass, which is why
        // the ordering tests assert the resulting positions rather than only cross-run
        // equality.
        Layout: new ProcessDescriptionLayout(
        SystemControls:
        [
            // Reverse-ordered on the key, like every other level of this fixture, so an
            // implementation that carried them in wire order is distinguishable.
            new ProcessDescriptionLayoutControl(
                "System.AreaPath", "Area", "WorkItemClassificationControl",
                false, true, true, false, 1),
            new ProcessDescriptionLayoutControl(
                "System.State", "State", "FieldControl", false, true, true, false, 0),
        ],
        Pages:
        [
            new ProcessDescriptionLayoutPage(
                "Page.Second", "Second", "custom", true, true, false, 1,
                [
                    new ProcessDescriptionLayoutSection("Section2", []),
                ]),
            new ProcessDescriptionLayoutPage(
                "Page.First", "First", "custom", true, false, false, 0,
                [
                    new ProcessDescriptionLayoutSection("Section2",
                    [
                        // 🔴 Order key and ALPHABETICAL order deliberately DISAGREE here:
                        // System.Title is order 0 and Custom.Zulu is order 1, so an
                        // implementation that sorted the layout alphabetically — a
                        // deterministic but WRONG choice, since a form's arrangement is its
                        // content — produces the opposite sequence and the assertion sees it.
                        // The wire order is descending on the key so trusting array order is
                        // also distinguishable.
                        new ProcessDescriptionLayoutGroup(
                            "Group.Late", "Late", true, true, false, 1,
                            [
                                new ProcessDescriptionLayoutControl(
                                    "Custom.Zulu", "Zulu", "FieldControl",
                                    false, true, false, false, 1),
                                new ProcessDescriptionLayoutControl(
                                    "System.Title", "Title", "FieldControl",
                                    false, true, true, false, 0),
                            ]),
                    ]),
                    new ProcessDescriptionLayoutSection("Section1",
                    [
                        new ProcessDescriptionLayoutGroup(
                            "Group.Early", "Early", true, false, false, 0,
                            [
                                new ProcessDescriptionLayoutControl(
                                    "System.Description", "Description", "HtmlFieldControl",
                                    false, true, true, false, 0),
                            ]),
                    ]),
                ]),
        ]));

    /// <summary>
    /// A type in <see cref="BuildSource"/>'s roster that carries the full hostile detail —
    /// rules, behaviours and layout included.
    /// </summary>
    private const string TypeWithRules = "Niflheim.CustomAlpha";

    /// <summary>
    /// The catalogue naming the two behaviours <see cref="HostileDetail"/>'s memberships
    /// reference.
    /// </summary>
    /// <remarks>
    /// Deliberately in the OPPOSITE order to the memberships, and with ranks that would sort
    /// them the other way round — so a document ordering memberships by rank rather than by
    /// reference name produces a different order and the ordering test can see it.
    /// </remarks>
    private static readonly IReadOnlyList<ProcessBehaviourSummary> Catalogue =
    [
        new ProcessBehaviourSummary("Custom.Alpha", "Alpha Backlog", 40),
        new ProcessBehaviourSummary("Custom.Zeta", "Zeta Backlog", 10),
    ];

    private static ScriptedDescriptionSource BuildSource(
        IReadOnlyList<string>? completionOrder = null,
        IReadOnlyList<string>? typeOrder = null)
    {
        // Reference names deliberately NOT in sorted order, so a document that emitted them
        // in arrival order would differ from one that sorted.
        var types = typeOrder ?? ["Niflheim.CustomZulu", "Microsoft.VSTS.WorkItemTypes.Task", TypeWithRules];

        return new ScriptedDescriptionSource(
            types,
            types.ToDictionary(t => t, HostileDetail))
        {
            CompletionOrder = completionOrder,
            BehaviourCatalogue = Catalogue,
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
            writer.Write(FlattenType(type));

        return writer.ToString();
    }

    /// <summary>
    /// A stable projection of ONE described type, walked to the leaf of every collection.
    /// </summary>
    /// <remarks>
    /// 🔴 Shared by <see cref="Flatten"/> and by the agent-surface equivalence test, so the two
    /// cannot come to compare different subsets of the document. Both need a PROJECTION rather
    /// than record equality: these records' collection members are compared by REFERENCE by
    /// compiler-generated equality, so <c>ShouldBe</c> fails on two structurally identical
    /// documents and passes on nothing useful.
    /// <para>
    /// Every ordered position of every collection is serialised. A projection that compared
    /// only counts, or stopped above the rules' own clauses or the layout's controls, would
    /// pass against an implementation whose order wobbled there — which is precisely the
    /// failure mode being defended against.
    /// </para>
    /// </remarks>
    private static string FlattenType(ProcessDescriptionType type)
    {
        var writer = new StringWriter();

        // 🔴 EVERY scalar the document carries for a type, including `description` and
        // `isDisabled`. Both reach the rendered file (verified against a live `-o json` run),
        // and both were missing from this projection until AB#239 — so a change that made a
        // type's description or disabled flag vary between two runs, or between two processes
        // sharing that type, compared EQUAL here while the emitted documents differed. Found
        // by patching the assembler to make a shared type's description depend on the roster
        // size: every byte-stability test stayed green. A projection blind to a member is a
        // hollow guard over exactly the property it advertises.
        writer.WriteLine(
            $"type={type.ReferenceName}|{type.Name}|{type.Description}|"
            + $"{type.Customization}|{type.Inherits}|{type.IsDisabled}");
        foreach (var field in type.Fields)
            writer.WriteLine(
                $"  field={field.ReferenceName}|{field.Name}|{field.Type}|"
                // 🔴 The MERGED requiredness, including every condition in its sorted
                // position. Flattening only the KIND would let the condition list's order
                // wobble between two runs while this projection still compared equal —
                // which is the byte-stability defect this projection exists to catch.
                + $"{FlattenRequiredness(field.Requiredness)}|"
                // 🔴 The value constraint, INCLUDING its values in their sorted positions.
                // Flattening only the kind would let the values' order wobble between two
                // runs while this projection still compared equal — which is exactly the
                // byte-stability defect this projection exists to catch, and picklist
                // values arrive from the server in author order.
                + $"{FlattenValueConstraint(field.ValueConstraint)}|{field.DefaultValue}|"
                + $"{field.Customization}|{field.Description}");
        foreach (var state in type.States)
            writer.WriteLine($"  state={state.Name}|{state.StateCategory}|{state.Order}");
        foreach (var transition in type.Transitions)
            writer.WriteLine($"  transition={transition.FromState}->{transition.ToState}");
        // 🔴 Every rule in its sorted position, with its conditions and actions in THEIR
        // sorted positions (AB#238). Emitting only a count would let the rule order — or a
        // rule's internal clause order — wobble between two runs while this projection
        // still compared equal, which is exactly the defect it exists to catch. Rules are
        // the largest such hazard in the document: ~54 per derived type, in server order.
        foreach (var rule in type.Rules)
            writer.WriteLine(
                $"  rule={rule.Name}|{rule.Customization.Kind}:{rule.Customization.Token}|"
                + $"{rule.IsDisabled}|"
                + string.Join("+", rule.Conditions.Select(static c =>
                    $"{c.ConditionType}:{c.Field}:{c.Value}"))
                + "|"
                + string.Join("+", rule.Actions.Select(static a =>
                    $"{a.ActionType}:{a.TargetField}:{a.Value}")));
        foreach (var behaviour in type.Behaviours)
            writer.WriteLine(
                $"  behaviour={behaviour.ReferenceName}|{behaviour.Name}|"
                + $"{behaviour.Rank}|{behaviour.IsDefault}");
        // 🔴 The layout walked to the LEAF, in position. Its order is its content, so a
        // projection that stopped at page level would compare equal across two documents
        // whose controls were arranged differently — hiding the one property a reader of
        // the layout actually asked about.
        //
        // 🔴 `layout=` is written even when the layout is null so the two cases are
        // distinguishable in this projection: a type with no layout and a type whose layout
        // has no pages must not flatten identically.
        writer.Write(FlattenLayout(type.Layout));
        foreach (var unfetched in type.Unfetched)
            writer.WriteLine($"  unfetched={unfetched}");

        return writer.ToString();
    }

    /// <summary>
    /// A total string form of a field's merged requiredness, including every condition in the
    /// order the assembler put it in.
    /// </summary>
    /// <remarks>
    /// 🔴 The ORDER of the conditions is included deliberately. Rules arrive in server order
    /// and the byte-stability tests are the only thing standing between that and a document
    /// that diffs dirty against itself; a projection that emitted only the kind, or that
    /// sorted here, would hide exactly that defect.
    /// </remarks>
    private static string FlattenRequiredness(FieldRequiredness requiredness)
        => requiredness.Kind
            + "["
            + string.Join(";", requiredness.Conditions.Select(static c =>
                string.Join("+", c.Clauses.Select(static cl =>
                    $"{cl.ConditionType}:{cl.Field}:{cl.Value}"))))
            + "]";

    /// <summary>
    /// A total string form of a field's value constraint, including every value in the order
    /// the assembler put it in.
    /// </summary>
    /// <remarks>
    /// 🔴 The ORDER of the values is included deliberately, for the same reason the conditions'
    /// is: picklist items arrive in the order whoever authored the list happened to type them,
    /// and the byte-stability tests are the only thing standing between that and a document
    /// that diffs dirty against itself. A projection that emitted only the kind, or that
    /// sorted here, would hide exactly that defect.
    /// </remarks>
    private static string FlattenValueConstraint(FieldValueConstraint constraint)
        => $"{constraint.Kind}({constraint.ListName})[{string.Join(",", constraint.Values)}]";

    /// <summary>
    /// A total string form of a form layout, walked to the LEAF in the order the assembler put
    /// it in.
    /// </summary>
    /// <remarks>
    /// 🔴 The layout's ORDER IS ITS CONTENT, so a projection that stopped at page level would
    /// compare equal across two documents whose controls were arranged differently — hiding
    /// the one property a reader of the layout actually asked about.
    /// <para>
    /// 🔴 The <c>&lt;none&gt;</c> / <c>&lt;present&gt;</c> marker is written even for a null
    /// layout so the two cases stay distinguishable: a type whose layout could not be read and
    /// a type whose layout has no pages must not flatten identically.
    /// </para>
    /// <para>
    /// Also used to compare two documents' layouts directly, because
    /// <c>ProcessDescriptionLayout</c> is a record whose collection members compare by
    /// REFERENCE — so <c>ShouldBe</c> on the layout itself would fail on two structurally
    /// identical documents and pass on nothing useful.
    /// </para>
    /// </remarks>
    private static string FlattenLayout(ProcessDescriptionLayout? layout)
    {
        var writer = new StringWriter();
        writer.WriteLine($"  layout={(layout is null ? "<none>" : "<present>")}");

        // 🔴 The system controls are part of the layout and therefore part of this projection.
        // Omitting them would let their order — or their presence at all — wobble between two
        // runs while the comparison still passed, which is the defect it exists to catch.
        foreach (var control in layout?.SystemControls ?? [])
            writer.WriteLine(
                $"    systemControl={control.Id}|{control.Label}|{control.ControlType}|"
                + $"{control.ReadOnly}|{control.Visible}|{control.Inherited}|"
                + $"{control.IsContribution}|{control.Order}");

        foreach (var page in layout?.Pages ?? [])
        {
            writer.WriteLine(
                $"    page={page.Id}|{page.Label}|{page.PageType}|{page.Visible}|"
                + $"{page.Inherited}|{page.IsContribution}|{page.Order}");
            foreach (var section in page.Sections)
            {
                writer.WriteLine($"      section={section.Id}");
                foreach (var group in section.Groups)
                {
                    writer.WriteLine(
                        $"        group={group.Id}|{group.Label}|{group.Visible}|"
                        + $"{group.Inherited}|{group.IsContribution}|{group.Order}");
                    foreach (var control in group.Controls)
                        writer.WriteLine(
                            $"          control={control.Id}|{control.Label}|"
                            + $"{control.ControlType}|{control.ReadOnly}|{control.Visible}|"
                            + $"{control.Inherited}|{control.IsContribution}|{control.Order}");
                }
            }
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
        var first = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var second = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

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

        var first = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var second = (await BuildAssembler(BuildSource()).AssembleAsync(null, later)).ShouldBeAssembled();

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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
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

        Flatten(inOrder.ShouldBeAssembled()).ShouldBe(Flatten(reversedResult.ShouldBeAssembled()));
    }

    /// <summary>
    /// 🔴 The same reversed-completion byte-identity, on a roster LARGER than the concurrency
    /// gate — so the semaphore itself is part of what decides completion order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The sibling test above cannot exercise the gate, and that is a real hole rather
    /// than a tidiness point (AB#239).</b> It uses three types against a bound of four, so
    /// every fetch is admitted immediately and the semaphore never makes a scheduling
    /// decision. It therefore proves byte-identity for an UNGATED fan-out only.
    /// </para>
    /// <para>
    /// Once the roster exceeds the bound the gate becomes a new ordering input: which types
    /// get in, and in which waves, is decided by release order rather than by the caller. The
    /// spec names parallelism as "what most plausibly breaks test 1", so the ordering guard
    /// has to be asserted under the conditions parallelism actually runs in — a whole process
    /// is fourteen types against a bound of four, never three.
    /// </para>
    /// <para>
    /// Completion order is driven explicitly through the gates, never by wall-clock timing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_WithReversedCompletionOrderAcrossTheGate_ProducesTheIdenticalDocument()
    {
        var types = Enumerable
            .Range(0, ProcessDescriptionAssembler.MaxConcurrentTypeFetches * 3)
            .Select(i => $"Niflheim.CustomType{i:D2}")
            .ToArray();
        var reversed = types.Reverse().ToArray();

        // Preconditions: the roster genuinely exceeds the gate — otherwise this is the
        // sibling test again — and the two scripts really are different orders.
        types.Length.ShouldBeGreaterThan(ProcessDescriptionAssembler.MaxConcurrentTypeFetches);
        reversed.ShouldNotBe(types);

        var inOrderSource = new ScriptedDescriptionSource(
            types, types.ToDictionary(t => t, HostileDetail))
        {
            CompletionOrder = types,
            BehaviourCatalogue = Catalogue,
        };
        var inOrderTask = BuildAssembler(inOrderSource).AssembleAsync(null, FixedCapture);
        await inOrderSource.ReleaseInScriptedOrderAsync();
        var inOrder = await inOrderTask;

        var reversedSource = new ScriptedDescriptionSource(
            types, types.ToDictionary(t => t, HostileDetail))
        {
            CompletionOrder = reversed,
            BehaviourCatalogue = Catalogue,
        };
        var reversedTask = BuildAssembler(reversedSource).AssembleAsync(null, FixedCapture);
        await reversedSource.ReleaseInScriptedOrderAsync();
        var reversedResult = await reversedTask;

        var inOrderDocument = inOrder.ShouldBeAssembled();

        // Precondition: the gate really was contended, or this ran ungated after all.
        inOrderSource.MaxConcurrentDetailFetches.ShouldBe(
            ProcessDescriptionAssembler.MaxConcurrentTypeFetches);

        inOrderDocument.Types.Count.ShouldBe(types.Length);
        Flatten(inOrderDocument).ShouldBe(
            Flatten(reversedResult.ShouldBeAssembled()),
            "the gate decides which fetches run in which wave, so it is an ordering input — "
            + "the document must still be byte-identical whichever order they complete in");
    }

    // ═══════════════════════════════════════════════════════════════
    //  The concurrency mitigation itself — that it happens, and that
    //  it stays inside its bound
    // ═══════════════════════════════════════════════════════════════

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
        (await assembleTask).ShouldBeAssembled();
    }

    /// <summary>
    /// 🔴 The concurrent fan-out is BOUNDED, and the bound is the one the assembler declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Two-sided, and both sides are real defects (AB#239).</b> Serialising the fetches
    /// undoes the ruled latency mitigation; removing the bound is the obvious wrong reading of
    /// "parallelise them" and produces ~70 in-flight requests over this process's fourteen
    /// types, because each type's detail call issues five concurrent GETs. Throttling degrades
    /// exactly the answers the document exists to make trustworthy, so a 429 generator is not
    /// a faster document — it is a worse one.
    /// </para>
    /// <para>
    /// The sibling test above proves concurrency happens at all; this one proves it stays
    /// inside the gate. It asserts against
    /// <see cref="ProcessDescriptionAssembler.MaxConcurrentTypeFetches"/> rather than a
    /// literal, so raising the bound is a deliberate edit at the declaration rather than
    /// something that drifts silently past a test carrying its own copy of the number.
    /// </para>
    /// <para>
    /// 🔴 The roster is deliberately LARGER than the bound. With as many types as the gate
    /// permits, an ungated implementation and a gated one are indistinguishable, and the test
    /// would be a hollow guard.
    /// </para>
    /// <para>
    /// 🔴 <b>No wall-clock assertion, and no fixed sleep.</b> The spec forbids timing
    /// assertions as flaky theatre, and a fixed <c>Task.Delay</c> is one wearing a disguise:
    /// it hopes the defect shows up inside the window, so on a loaded machine it passes
    /// against exactly what it exists to catch. Instead every gate is held SHUT and the wait
    /// exits the INSTANT the bound is exceeded — so a violation reds immediately and
    /// deterministically, and the bounded deadline is paid only on the green path where the
    /// thing being waited for correctly never happens.
    /// </para>
    /// <para>
    /// 🔴 The high-water mark is read AFTER the run completes, so the assertion covers the
    /// whole run rather than a sampled window. With the gates shut a gated implementation can
    /// admit exactly as many calls as the semaphore permits and then stops; an ungated one
    /// issues the entire roster, and that overshoot is recorded permanently.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_WholeProcess_BoundsHowManyTypeFetchesRunAtOnce()
    {
        var bound = ProcessDescriptionAssembler.MaxConcurrentTypeFetches;
        var types = Enumerable
            .Range(0, bound * 3)
            .Select(i => $"Niflheim.CustomType{i:D2}")
            .ToArray();

        // Precondition: the roster genuinely exceeds the bound, or a gated and an ungated
        // implementation would behave identically here and this would be a hollow guard.
        types.Length.ShouldBeGreaterThan(bound);

        var source = new ScriptedDescriptionSource(
            types,
            types.ToDictionary(t => t, HostileDetail))
        {
            BehaviourCatalogue = Catalogue,
            // Every gate SHUT: no detail fetch may complete until the test releases it, so
            // the semaphore is the only thing deciding how many get in.
            CompletionOrder = types,
        };

        var assembleTask = BuildAssembler(source).AssembleAsync(null, FixedCapture);

        // Wait for the fan-out to fill the gate, then keep watching for an OVERSHOOT, exiting
        // the moment one appears. Stopwatch rather than DateTime.UtcNow so a clock adjustment
        // cannot move the deadline under us.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(2)
            && source.MaxConcurrentDetailFetches <= bound)
        {
            await Task.Delay(5);
        }

        await source.ReleaseInScriptedOrderAsync();
        var description = (await assembleTask).ShouldBeAssembled();

        description.Types.Count.ShouldBe(
            types.Length,
            "the gate throttles the fan-out; it must never drop a type from the document");

        source.MaxConcurrentDetailFetches.ShouldBe(
            bound,
            "the fan-out must fill the gate and never exceed it. Below the bound means the "
            + "fetches were serialised, undoing the ruled latency mitigation; above it means "
            + "the gate was removed — each type's detail call issues five concurrent GETs, so "
            + "an ungated projection over a whole process is a 429 generator, and throttling "
            + "degrades exactly the answers this document exists to make trustworthy");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 12 — no type argument describes every type, and a type's
    //  ABSENCE is visible between two documents
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 No type argument describes EVERY type in the process — not a subset, not a page.
    /// </summary>
    /// <remarks>
    /// Spec test 12 and Implementation Decision 3. Asserted against the source's own roster
    /// rather than a hardcoded count, so a fixture gaining a type cannot leave this test
    /// asserting a stale number and silently passing an assembler that dropped one.
    /// </remarks>
    [Fact]
    public async Task Assemble_WithNoTypeArgument_DescribesEveryTypeInTheProcess()
    {
        var source = BuildSource();
        var roster = (await source.GetTypesAsync())!.Select(t => t.ReferenceName).ToList();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description.ShouldNotBeNull();
        description.Types.Select(t => t.ReferenceName).ShouldBe(
            [.. roster.OrderBy(x => x, StringComparer.Ordinal)],
            "the whole-process path describes every type the process reports, in the "
            + "assembler's ordinal order");
    }

    /// <summary>
    /// 🔴 A type present in one process and absent from another is VISIBLE as a difference
    /// between the two documents — the reason the whole-process default exists at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Spec test 12's absence half, and it is the load-bearing half.</b> Implementation
    /// Decision 3's decisive argument is not convenience: <b>a per-type document cannot
    /// express a type's ABSENCE.</b> A type in one process and not the other is exactly the
    /// difference the comparison case exists to find, and only a whole-process document can
    /// show it.
    /// </para>
    /// <para>
    /// 🔴 Asserted as a real DIFF rather than as "the second document lacks the type". The
    /// latter passes against an assembler whose two documents differ everywhere for unrelated
    /// reasons. This removes the absent type's own block from the richer document and asserts
    /// what remains is SEQUENCE-EQUAL to the thinner one — which is exactly what a reader with
    /// an ordinary diff tool sees, and is what makes ruling S2's promise real.
    /// </para>
    /// <para>
    /// 🔴 Sequence equality, deliberately, and not a set difference. <c>Except</c> dedupes and
    /// is blind to both order and multiplicity, so it would pass against a document that
    /// emitted the shared types in a different ORDER, or emitted one of them twice — the
    /// byte-stability defects this very comparison is invoked to rule out. It is also
    /// self-defeating on this fixture, whose types emit byte-identical lines
    /// (<c>state=Done|Completed|3</c>), so the absent type's block would be largely absorbed
    /// by the shared types' identical lines and the assertion would check far less than it
    /// appeared to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_TypeAbsentFromOneProcess_IsTheOnlyDifferenceBetweenTheDocuments()
    {
        var shared = new[] { "Niflheim.CustomZulu", "Microsoft.VSTS.WorkItemTypes.Task" };
        var withExtra = new[] { "Niflheim.CustomZulu", TypeWithRules, "Microsoft.VSTS.WorkItemTypes.Task" };

        // Precondition: the two rosters genuinely differ by exactly one type, or this test is
        // comparing a document with itself.
        withExtra.Except(shared, StringComparer.Ordinal).ShouldBe([TypeWithRules]);
        shared.Except(withExtra, StringComparer.Ordinal).ShouldBeEmpty();

        var richer = (await BuildAssembler(BuildSource(typeOrder: withExtra))
            .AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var thinner = (await BuildAssembler(BuildSource(typeOrder: shared))
            .AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        richer.ShouldNotBeNull();
        thinner.ShouldNotBeNull();

        // The absent type shows up as a difference in the type roster.
        richer.Types.Select(t => t.ReferenceName).ShouldContain(TypeWithRules);
        thinner.Types.Select(t => t.ReferenceName).ShouldNotContain(TypeWithRules);

        // 🔴 …and it is the ONLY difference, asserted as a diff. The absent type's block is
        // removed from the richer document and the remainder must be sequence-equal to the
        // thinner one — same lines, same order, same number of them.
        var absentBlock = FlattenType(richer.Types.Single(t => t.ReferenceName == TypeWithRules));
        var richerText = Flatten(richer);

        // Precondition: the block is genuinely present and contributes content, or the
        // removal below is a no-op and the assertion is vacuous.
        absentBlock.ShouldNotBeNullOrEmpty();
        richerText.ShouldContain(absentBlock);

        richerText.Replace(absentBlock, string.Empty, StringComparison.Ordinal).ShouldBe(
            Flatten(thinner),
            "a type present in one process and absent from another must be the ONLY difference "
            + "between the two documents — everything else has to diff clean, or the absence "
            + "is buried in noise and the comparison case this document exists for fails");
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
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

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

        var description = (await BuildAssembler(source)
            .AssembleAsync(["Niflheim.CustomAlpha"], FixedCapture)).ShouldBeAssembled();

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
        var whole = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var single = (await BuildAssembler(BuildSource())
            .AssembleAsync(["Niflheim.CustomAlpha"], FixedCapture)).ShouldBeAssembled();

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
        // 🔴 Every remaining member compared through the flattened projection rather than with
        // record equality — including the AB#238 additions, so the agent surface and the CLI
        // cannot drift apart on exactly the content this ticket adds. The projection is
        // necessary and not merely convenient: these records' collection members are compared
        // by REFERENCE by compiler-generated equality, so a nested List (which a conditionally
        // required field's conditions are) makes `ShouldBe` fail on two structurally identical
        // documents and pass on nothing useful. The projection walks every ordered position of
        // every collection to the leaf.
        FlattenType(fromSingle).ShouldBe(
            FlattenType(fromWhole),
            "the agent surface must return the SAME document with fewer types in it — same "
            + "shape, same ordering, right down to the rules, memberships and layout");
    }

    /// <summary>
    /// An unknown type is a hard error naming what was asked for, not an empty document — and
    /// it arrives as a union ARM rather than as a thrown exception (AB#244).
    /// </summary>
    [Fact]
    public async Task Assemble_UnknownType_ReturnsTypeNotFoundNamingTheTypeAskedFor()
    {
        var outcome = await BuildAssembler(BuildSource())
            .AssembleAsync(["Niflheim.NoSuchType"], FixedCapture);

        // 🔴 Pattern-matched, not ShouldBeOfType<T>(): a C# union is a WRAPPER, so the runtime
        // type is ProcessDescriptionResult and never the case type. Same trap MergeResult sets.
        var notFound = outcome.ShouldBeTypeNotFound();
        notFound.TypeReferenceName.ShouldBe("Niflheim.NoSuchType");

        // And it is NOT the success arm — asserting the case above would be satisfied by a
        // union that carried both, which this one cannot, but the claim is worth pinning.
        (outcome is ProcessDescriptionAssembled).ShouldBeFalse();
    }

    /// <summary>
    /// The header carries org, project, process, descriptor version, and the pinned
    /// api-version per route.
    /// </summary>
    [Fact]
    public async Task Assemble_HeaderCarriesProvenanceAndPinnedRouteVersions()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

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
    /// 🔴 The document declares every content item it does NOT yet carry — and declares no gap
    /// it has closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter and they pull in opposite directions. Declaring a CLOSED gap warns a
    /// reader off an answer the document does give; failing to declare an OPEN one lets a
    /// silent omission pass as completeness. AB#237 originally emptied this list, which turned
    /// three genuinely-absent Decision 4 items into an affirmative claim that nothing was
    /// missing — caught in review, and this test is the guard against repeating it.
    /// </para>
    /// <para>
    /// 🔴 <b>AB#238 empties the list, and the assertion below is deliberately NOT just
    /// "empty".</b> "Nothing is declared" is what an implementation that deleted the mechanism
    /// produces too. So this test enumerates every Decision 4 content item and asserts the
    /// document actually CARRIES it — which is what earns the empty list. An implementation
    /// that emptied the list without shipping the content reds on the carry assertions, not
    /// on the count, which is the honest place for it to fail.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_HeaderDeclaresTheKnownGapsWithTheirTickets()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description.ShouldNotBeNull();

        var gaps = description.Header.KnownGaps;

        // 🔴 AB#237 landed: the constraint merge is done, so this reservation must be gone.
        gaps.ShouldNotContain(
            g => g.Subject == "picklistValues",
            "value constraints are merged from the org field source now — declaring it a gap "
            + "would warn a reader off an answer this document does give");
        gaps.ShouldNotContain(g => g.TrackedIn == "AB#237");

        // 🔴 AB#236 landed too: same reasoning, asserted so a revert of either shows up here.
        gaps.ShouldNotContain(
            g => g.Subject == "conditionalRequiredness",
            "requiredness is merged from the rules source now — declaring it a gap would warn "
            + "a reader off an answer this document does give");
        gaps.ShouldNotContain(g => g.TrackedIn == "AB#236");

        // 🔴 AB#238's three reservations are gone because the content shipped — and the
        // removal is EARNED, not merely asserted. Each item is checked against the document
        // itself, so an implementation that deleted the reservations without shipping the
        // content reds HERE, on the missing content, rather than passing a bare count check.
        gaps.ShouldNotContain(g => g.Subject == "rules");
        gaps.ShouldNotContain(g => g.Subject == "behaviourMembership");
        gaps.ShouldNotContain(g => g.Subject == "formLayout");

        var type = description.Types.Single(t => t.ReferenceName == TypeWithRules);
        type.Rules.ShouldNotBeEmpty("the rules reservation was removed, so rules must be carried");
        type.Behaviours.ShouldNotBeEmpty(
            "the behaviourMembership reservation was removed, so membership must be carried");
        type.Layout.ShouldNotBeNull(
            "the formLayout reservation was removed, so the layout must be carried");

        // 🔴 …and the layout is carried WHOLE. An earlier draft reached every one of these and
        // then dropped them in the renderer while this list claimed completeness, so the
        // reservation's removal is tied to the members actually being present.
        var page = type.Layout.Pages[0];
        page.Order.ShouldNotBeNull("the arrangement key is part of the layout, not scaffolding");
        page.Sections.SelectMany(s => s.Groups).ShouldNotBeEmpty();

        // 🔴 The ONE surviving reservation: the rule id is reachable and deliberately not
        // carried, so it is DECLARED. An empty list here would claim the document omits
        // nothing reachable, which is false — and a false completeness claim is the specific
        // failure this mechanism exists to prevent.
        gaps.Select(g => g.Subject).ShouldBe(
            ["ruleIdentity"],
            "an omission with a good reason is still an omission, and belongs in the artifact "
            + "rather than only in a doc comment");

        // Every gap names a ticket a reader can go and look up — a reservation with no tracker
        // is a dead end rather than a promise. Vacuous while the list is empty, and kept so it
        // is already in place the next time something ships incomplete.
        gaps.ShouldAllBe(g => !string.IsNullOrWhiteSpace(g.TrackedIn));
        gaps.ShouldAllBe(g => !string.IsNullOrWhiteSpace(g.Detail));
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

        var afterFirstTypes = source.TypeListCallCount;
        var afterFirstConstraints = source.ConstraintCallCount;

        await assembler.AssembleAsync(null, FixedCapture);

        afterFirstTypes.ShouldBeGreaterThan(0);
        source.TypeListCallCount.ShouldBeGreaterThan(
            afterFirstTypes,
            "the second run must re-fetch — a cache would trade away the one property the "
            + "artifact exists to have");

        // 🔴 The picklist source is covered too (AB#237). It is the one source fetched ONCE
        // per run rather than once per type, which makes it the most tempting thing in this
        // class to memoize across runs — and the ruling is no caching of ANY kind, not "no
        // caching of the sources that happened to exist when the rule was written". A new
        // source added without extending this test is how the ruling quietly stops holding.
        afterFirstConstraints.ShouldBeGreaterThan(0);
        source.ConstraintCallCount.ShouldBeGreaterThan(
            afterFirstConstraints,
            "the second run must re-resolve value constraints — a stale picklist is a stale "
            + "claim about what the server accepts");
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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var field = description.Types[0].Fields.ShouldHaveSingleItem();
        field.ReferenceName.ShouldBe("System.Title");
        field.DefaultValue.ShouldBe("a default");
        // The MERGED answer, not the fields route's boolean. This field is required with no
        // condition attached and no rule touches it, so it stays unconditional.
        field.Requiredness.Kind.ShouldBe(FieldRequirednessKind.Always);
        field.Requiredness.Conditions.ShouldBeEmpty();
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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var genuinelyEmpty = description.Types.Single(t => t.ReferenceName == "Niflheim.CustomAlpha");
        var couldNotRead = description.Types.Single(t => t.ReferenceName == "Niflheim.CustomZulu");

        // Precondition: both carry empty collections, so the ONLY thing that can tell them
        // apart is the explicit label. Without this check the test could pass on a difference
        // that is not the one under test.
        genuinelyEmpty.Fields.ShouldBeEmpty();
        couldNotRead.Fields.ShouldBeEmpty();

        genuinelyEmpty.Unfetched.ShouldBeEmpty();
        // 🔴 'rules' is in the list: requiredness is merged from the rules route (AB#236), so
        // a whole-detail failure leaves the requiredness answer unreadable too.
        // 🔴 'behaviours' and 'formLayout' joined it in AB#238 — both are per-type fetches
        // inside the detail call, so a whole-detail failure loses them too, and unlabelled
        // they would read as "belongs to no backlog" and "has an empty form".
        couldNotRead.Unfetched.ShouldBe(
            ["behaviours", "fields", "formLayout", "rules", "states", "transitions"]);
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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
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

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

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

        var description = (await BuildAssembler(source).AssembleAsync(
            ["Niflheim.CustomAlpha", "niflheim.customalpha", "Niflheim.CustomAlpha"],
            FixedCapture)).ShouldBeAssembled();

        description.ShouldNotBeNull();
        description.Types.Count.ShouldBe(1);
        description.Types[0].ReferenceName.ShouldBe("Niflheim.CustomAlpha");

        // And it was fetched once, not three times — the duplicate was collapsed before the
        // fan-out, not merely de-duplicated in the output.
        source.RequestedTypes.Count(t => string.Equals(
            t, "Niflheim.CustomAlpha", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    /// <summary>
    /// 🔴 A rule whose <c>targetField</c> differs only in CASING still makes the field required.
    /// </summary>
    /// <remarks>
    /// The join is across two routes — the rules route's <c>targetField</c> against the fields
    /// route's <c>referenceName</c> — and this route family is already known to be inconsistent
    /// about spelling (the <c>$</c> sigil is the same class of problem). An ordinal-exact join
    /// would drop the rule and report the field as not-required, which is byte-identical to a
    /// field nobody makes required and carries no <c>unfetched</c> label to catch it: the exact
    /// silent lie AB#236 removes, reintroduced as a failed JOIN rather than a failed fetch.
    /// <para>
    /// Every other reference-name comparison in this layer is ordinal-case-insensitive for the
    /// same reason, including <c>SelectTypes</c> in this very class.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_RuleTargetingTheFieldInDifferentCasing_StillMakesItRequired()
    {
        var source = BuildMergeSource(
        [
            // Lower-cased target against the fields route's "Custom.WayfinderAnswer".
            MakeRequiredWhen("custom.wayfinderanswer", "Done"),
        ]);

        // Precondition: read through the seam — the two spellings must genuinely differ, or
        // this test proves nothing about the comparer.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        var target = detail!.Rules!.Single().Actions.Single().TargetField;
        var onTheFieldsRoute = detail.Fields
            .Single(f => string.Equals(f.ReferenceName, target, StringComparison.OrdinalIgnoreCase))
            .ReferenceName;

        // Ordinal-exact inequality: the two spellings differ ONLY in casing, which is the
        // whole point — an ordinal-exact join would miss the rule.
        string.Equals(target, onTheFieldsRoute, StringComparison.Ordinal).ShouldBeFalse();
        string.Equals(target, onTheFieldsRoute, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .Requiredness.Kind.ShouldBe(
                FieldRequirednessKind.Conditional,
                "a casing difference between the two routes must not silently drop the rule");
    }

    /// <summary>
    /// 🔴 Two fields identical on reference name, display name and type but differing in
    /// requiredness are ordered deterministically.
    /// </summary>
    /// <remarks>
    /// The field sort's own remarks say the tiebreak chain must be total: if the server ever
    /// returns two rows agreeing on the identifying members, ordering would otherwise fall
    /// through <c>OrderBy</c>'s stability to WIRE order — reintroducing the non-determinism
    /// this class exists to remove, silently. Adding <c>Requiredness</c> to the document
    /// without extending the chain would have left exactly that hole, so it is pinned here.
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldsDifferingOnlyInRequiredness_AreOrderedDeterministically()
    {
        // Two rows agreeing on (ReferenceName, Name, Type) — the identifying members — and
        // differing only in members further down the chain.
        ProcessTypeField Duplicate(bool required, string description) => new(
            "Custom.Duplicated", "Duplicated", "string", null, required, "custom", false, description);

        ProcessTypeDetail Detail(bool firstRequired) => new(
            Fields:
            [
                Duplicate(firstRequired, firstRequired ? "required one" : "loose one"),
                Duplicate(!firstRequired, !firstRequired ? "required one" : "loose one"),
            ],
            States: [],
            Transitions: [],
            Unfetched: null,
            Rules: []);

        var forwardSource = new ScriptedDescriptionSource(
            ["Niflheim.Grilling"],
            new Dictionary<string, ProcessTypeDetail> { ["Niflheim.Grilling"] = Detail(true) });
        var reversedSource = new ScriptedDescriptionSource(
            ["Niflheim.Grilling"],
            new Dictionary<string, ProcessTypeDetail> { ["Niflheim.Grilling"] = Detail(false) });

        // Precondition: the two fixtures really do present the rows in opposite WIRE order, and
        // the rows really are identical on the three identifying members — otherwise an earlier
        // tiebreak resolves it and the new ones are never exercised.
        var forwardWire = (await forwardSource.GetTypeDetailAsync("Niflheim.Grilling"))!.Fields;
        var reversedWire = (await reversedSource.GetTypeDetailAsync("Niflheim.Grilling"))!.Fields;

        forwardWire.Select(f => f.RequiredUnconditionally)
            .ShouldBe(reversedWire.Select(f => f.RequiredUnconditionally).Reverse());
        forwardWire.Select(f => (f.ReferenceName, f.Name, f.Type)).Distinct().Count().ShouldBe(1);

        var forward = (await BuildAssembler(forwardSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var reversed = (await BuildAssembler(reversedSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        Flatten(forward!).ShouldBe(
            Flatten(reversed!),
            "two rows differing only below the identifying members must not order by wire order");
    }

    /// <summary>
    /// An unresolvable process yields the <see cref="ProcessIdentityUnresolved"/> arm, not a
    /// document and not the arm that means the type list failed.
    /// </summary>
    [Fact]
    public async Task Assemble_WhenProcessCannotBeResolved_ReturnsProcessUnresolved()
    {
        var source = new ScriptedDescriptionSource([], []) { Identity = null };

        var outcome = await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        (outcome is ProcessIdentityUnresolved).ShouldBeTrue(
            $"expected ProcessIdentityUnresolved, got {outcome.Value?.GetType().Name}");
    }

    /// <summary>
    /// 🔴 An unfetchable type list is a DIFFERENT arm from an unresolvable process (AB#244).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion the old shape could not make. Both outcomes were <c>null</c>, so
    /// any test of either was satisfied by the other and the distinction had no test at all —
    /// which is exactly how the command came to collapse them into one message.
    /// </para>
    /// <para>
    /// PRECONDITION asserted explicitly: the process identity DOES resolve on this fixture.
    /// Without it a source that failed to resolve identity would take the earlier branch and
    /// this test would pass while never reaching the code it names.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_WhenTypeListCannotBeFetched_ReturnsTypesUnfetchableNotProcessUnresolved()
    {
        var source = new ScriptedDescriptionSource([], []) { TypeListUnfetchable = true };

        // PRECONDITION: identity resolves, so the failure under test is the type list's.
        (await source.GetProcessIdentityAsync()).ShouldNotBeNull();

        var outcome = await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        (outcome is ProcessTypesUnfetchable).ShouldBeTrue(
            $"expected ProcessTypesUnfetchable, got {outcome.Value?.GetType().Name}");

        // 🔴 The load-bearing half: the two failures are DISTINGUISHABLE. Under the old
        // null-returning shape both were the same value, so this could not be asserted.
        (outcome is ProcessIdentityUnresolved).ShouldBeFalse(
            "an unfetchable type list must not be reported as an unresolvable process — the "
            + "two have different remedies (AB#244)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 3 — requiredness that does not lie (AB#236)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The live shape: <c>when State = Done → makeRequired Custom.WayfinderAnswer</c>.</summary>
    private static ProcessRule MakeRequiredWhen(string targetField, string state) => new(
        Conditions: [new RuleCondition("when", "System.State", state)],
        Actions: [new RuleAction("makeRequired", targetField, null)],
        IsDisabled: false);

    /// <summary>
    /// The AB#236 fixture: one field the FIELDS source calls not-required and the RULES source
    /// makes required, and one the fields source calls required outright.
    /// </summary>
    /// <remarks>
    /// 🔴 The two-source disagreement is the whole point, and it is asserted as a precondition
    /// in every test that uses this — never merely commented. If the conditional field were
    /// unconditionally required in the fixture, the merge would never run and the tests would
    /// pass against unfixed code.
    /// </remarks>
    private static ScriptedDescriptionSource BuildMergeSource(
        IReadOnlyList<ProcessRule>? rules = null)
    {
        var types = new[] { "Niflheim.Grilling" };

        return new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Grilling"] = new(
                Fields:
                [
                    // 🔴 required: false on the fields route — verified live, this is exactly
                    // what Custom.WayfinderAnswer reports there while /rules makes it
                    // mandatory at Done.
                    new ProcessTypeField(
                        "Custom.WayfinderAnswer", "Answer", "html", null, false, "custom", false, ""),
                    new ProcessTypeField(
                        "System.Title", "Title", "string", null, true, "system", false, ""),
                    new ProcessTypeField(
                        "Custom.Untouched", "Untouched", "string", null, false, "custom", false, ""),
                ],
                States: [new ProcessTypeState("Done", "Completed", 3, "339947", "custom", false)],
                Transitions: [new ProcessTypeTransition("", "Done")],
                Unfetched: null,
                Rules: rules ?? [MakeRequiredWhen("Custom.WayfinderAnswer", "Done")]),
        });
    }

    /// <summary>
    /// 🔴 THE ticket. A field whose ONLY requiredness comes from a rule renders as
    /// required-under-that-condition — never as simply not-required.
    /// </summary>
    /// <remarks>
    /// Fails against the obvious implementation that reads requiredness from the fields source
    /// alone, which is what shipped before AB#236. It fails SILENTLY in production, which is
    /// why it is asserted here: the fields route reports <c>required: null</c> for
    /// <c>Custom.WayfinderAnswer</c> while the rules route carries a <c>makeRequired</c>
    /// action for it, so a document built from one source is wrong about exactly the fields a
    /// caller most needs.
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldRequiredOnlyByARule_IsNotReportedAsNotRequired()
    {
        var source = BuildMergeSource();

        // 🔴 THE PRECONDITION, asserted rather than assumed. The two sources must genuinely
        // disagree about this field, or the merge never runs and this test passes against
        // unfixed code — the fixture hazard the spec names for exactly this test.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail.ShouldNotBeNull();

        var fromFields = detail.Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer");
        fromFields.RequiredUnconditionally.ShouldBeFalse(
            "the FIELDS source must say not-required, or the merge is not exercised");

        detail.Rules.ShouldNotBeNull();
        detail.Rules.ShouldContain(
            r => r.Actions.Any(a => a.ActionType == "makeRequired"
                && a.TargetField == "Custom.WayfinderAnswer"),
            "the RULES source must say required, or the two sources do not disagree");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var merged = description.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.WayfinderAnswer");

        merged.Requiredness.Kind.ShouldBe(
            FieldRequirednessKind.Conditional,
            "a field made mandatory by a rule must not render as simply not-required");

        // …and the condition is CARRIED, not merely flagged. "Conditional" with no condition
        // is a warning the reader cannot act on: it names no state, no field, no value.
        var condition = merged.Requiredness.Conditions.ShouldHaveSingleItem();
        var clause = condition.Clauses.ShouldHaveSingleItem();
        clause.ConditionType.ShouldBe("when");
        clause.Field.ShouldBe("System.State");
        clause.Value.ShouldBe("Done");
    }

    /// <summary>
    /// An unconditionally-required field still renders as required, and NOT as conditional.
    /// </summary>
    /// <remarks>
    /// The other half of the pair. Without it, an implementation that reported every field as
    /// conditional — or that lost the unconditional case in the merge — would pass the test
    /// above. Both directions of the lie are defended: reporting the conditional case as
    /// not-required is the AB#236 defect; reporting the unconditional case as conditional
    /// would tell a caller it may omit a field the server will reject.
    /// </remarks>
    [Fact]
    public async Task Assemble_UnconditionallyRequiredField_StillRendersAsRequired()
    {
        var source = BuildMergeSource();

        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        // Precondition: the fixture really carries an unconditionally-required field, and no
        // rule touches it — so the assertion is about the merge preserving it, not about a
        // rule reinstating it.
        detail!.Fields.Single(f => f.ReferenceName == "System.Title")
            .RequiredUnconditionally.ShouldBeTrue();
        detail.Rules!.SelectMany(r => r.Actions)
            .ShouldNotContain(a => a.TargetField == "System.Title");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        var title = description!.Types[0].Fields.Single(f => f.ReferenceName == "System.Title");
        title.Requiredness.Kind.ShouldBe(FieldRequirednessKind.Always);
        title.Requiredness.Conditions.ShouldBeEmpty();

        // A field neither source makes mandatory is reported as such — proving the merge is
        // not simply marking everything required.
        var untouched = description.Types[0].Fields.Single(f => f.ReferenceName == "Custom.Untouched");
        untouched.Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);
    }

    /// <summary>
    /// 🔴 A field required BOTH unconditionally and by a rule stays unconditional.
    /// </summary>
    /// <remarks>
    /// Downgrading it to conditional would be a lie in the DANGEROUS direction: a caller would
    /// read "required only at Done" and omit the field, and the server would reject the create.
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldRequiredByBothSources_ReportsUnconditionalNotConditional()
    {
        var source = BuildMergeSource(
        [
            MakeRequiredWhen("Custom.WayfinderAnswer", "Done"),
            // A rule ALSO targeting the unconditionally-required field.
            MakeRequiredWhen("System.Title", "Done"),
        ]);

        // Precondition: both a rule and the fields source claim System.Title.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Fields.Single(f => f.ReferenceName == "System.Title")
            .RequiredUnconditionally.ShouldBeTrue();
        detail.Rules!.SelectMany(r => r.Actions)
            .ShouldContain(a => a.TargetField == "System.Title");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "System.Title")
            .Requiredness.Kind.ShouldBe(
                FieldRequirednessKind.Always,
                "an unconditionally-required field must not be downgraded to conditional — a "
                + "caller would omit it and the server would reject the create");
    }

    /// <summary>A DISABLED rule does not make a field required.</summary>
    /// <remarks>
    /// A disabled rule does not fire on the server, so reporting requiredness from one is a
    /// false positive — the mirror-image failure of the defect this ticket fixes.
    /// </remarks>
    [Fact]
    public async Task Assemble_DisabledMakeRequiredRule_DoesNotMakeTheFieldRequired()
    {
        var source = BuildMergeSource(
        [
            new ProcessRule(
                Conditions: [new RuleCondition("when", "System.State", "Done")],
                Actions: [new RuleAction("makeRequired", "Custom.WayfinderAnswer", null)],
                IsDisabled: true),
        ]);

        // Precondition: the rule really is disabled AND really does target the field — so the
        // test is about the disabled flag and not about a fixture that forgot the rule.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        var rule = detail!.Rules.ShouldHaveSingleItem();
        rule.IsDisabled.ShouldBeTrue();
        rule.Actions.ShouldContain(a => a.TargetField == "Custom.WayfinderAnswer");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);
    }

    /// <summary>
    /// An UNCONDITIONED <c>makeRequired</c> rule yields unconditional requiredness, not a
    /// condition with nothing in it.
    /// </summary>
    /// <remarks>
    /// "Conditional" with an empty condition would print a reservation naming no state, no
    /// field and no value — a warning a reader cannot act on, which is a different flavour of
    /// the same dishonesty this ticket removes.
    /// </remarks>
    [Fact]
    public async Task Assemble_UnconditionedMakeRequiredRule_YieldsUnconditionalRequiredness()
    {
        var source = BuildMergeSource(
        [
            new ProcessRule(
                Conditions: [],
                Actions: [new RuleAction("makeRequired", "Custom.WayfinderAnswer", null)],
                IsDisabled: false),
        ]);

        // Precondition: the fields source says not-required and the rule carries no condition.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .RequiredUnconditionally.ShouldBeFalse();
        detail.Rules.ShouldHaveSingleItem().Conditions.ShouldBeEmpty();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        var merged = description!.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.WayfinderAnswer");
        merged.Requiredness.Kind.ShouldBe(FieldRequirednessKind.Always);
        merged.Requiredness.Conditions.ShouldBeEmpty();
    }

    /// <summary>
    /// A non-<c>makeRequired</c> action does not make a field required.
    /// </summary>
    /// <remarks>
    /// The rules route carries <c>copyValue</c>, <c>makeReadOnly</c>, <c>disallowValue</c> and
    /// more — ~54 of them on a derived type. An implementation that treated any rule targeting
    /// a field as making it required would report almost every field on a derived type as
    /// conditionally required, which is noise dressed as a truth claim.
    /// </remarks>
    [Fact]
    public async Task Assemble_RuleWithADifferentAction_DoesNotMakeTheFieldRequired()
    {
        var source = BuildMergeSource(
        [
            new ProcessRule(
                Conditions: [new RuleCondition("when", "System.State", "Done")],
                Actions: [new RuleAction("makeReadOnly", "Custom.WayfinderAnswer", null)],
                IsDisabled: false),
        ]);

        // Precondition: a rule DOES target the field — just not with makeRequired.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Rules.ShouldHaveSingleItem().Actions
            .ShouldContain(a => a.TargetField == "Custom.WayfinderAnswer"
                && a.ActionType != "makeRequired");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);
    }

    /// <summary>
    /// The <c>$</c> sigil some routes prefix onto an action verb does not hide a
    /// <c>makeRequired</c>.
    /// </summary>
    /// <remarks>
    /// The rules payload is not consistent about the sigil across api-versions and
    /// customization types, and this repo's own <c>DependentFieldReconciler</c> already trims
    /// it for exactly that reason. Missing a <c>$makeRequired</c> would silently reinstate the
    /// defect AB#236 fixes for whichever rules carry it.
    /// </remarks>
    [Fact]
    public async Task Assemble_MakeRequiredWithADollarSigil_IsStillRecognised()
    {
        var source = BuildMergeSource(
        [
            new ProcessRule(
                Conditions: [new RuleCondition("$when", "System.State", "Done")],
                Actions: [new RuleAction("$makeRequired", "Custom.WayfinderAnswer", null)],
                IsDisabled: false),
        ]);

        // Precondition: the fixture really does use the sigil form.
        var detail = await source.GetTypeDetailAsync("Niflheim.Grilling");
        detail!.Rules.ShouldHaveSingleItem().Actions
            .ShouldContain(a => a.ActionType.StartsWith('$'));

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        var merged = description!.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.WayfinderAnswer");
        merged.Requiredness.Kind.ShouldBe(FieldRequirednessKind.Conditional);
        // The condition verb is carried with the sigil trimmed too, so the same rule cannot
        // diff dirty between two documents merely because one route spelled it with the sigil.
        merged.Requiredness.Conditions[0].Clauses[0].ConditionType.ShouldBe("when");
    }

    /// <summary>
    /// 🔴 Conditions are ORDERED DETERMINISTICALLY, not carried in server order.
    /// </summary>
    /// <remarks>
    /// Byte-stability is the single most important property of this feature, and a rule list
    /// arrives in SERVER order — which is not promised stable. This drives the same rules
    /// through in two different orders and asserts the merged document is identical, which is
    /// what the whole-document byte-stability test cannot see because it feeds one fixture.
    /// </remarks>
    [Fact]
    public async Task Assemble_RulesArrivingInADifferentOrder_ProduceTheIdenticalDocument()
    {
        ProcessRule[] rules =
        [
            MakeRequiredWhen("Custom.WayfinderAnswer", "Done"),
            MakeRequiredWhen("Custom.WayfinderAnswer", "Aborted"),
            MakeRequiredWhen("Custom.Untouched", "Doing"),
        ];
        var reversed = rules.Reverse().ToArray();

        var forwardSource = BuildMergeSource(rules);
        var backwardSource = BuildMergeSource(reversed);

        // Precondition: read back through the SEAM, so a fixture-builder regression that
        // dropped or reordered the rules is caught. Checking the literals above cannot fail.
        // Both orders must genuinely reach the assembler differently, and more than one rule
        // must target the same field — a single-rule fixture cannot detect an ordering defect.
        var forwardOnTheWire = (await forwardSource.GetTypeDetailAsync("Niflheim.Grilling"))!.Rules!;
        var backwardOnTheWire = (await backwardSource.GetTypeDetailAsync("Niflheim.Grilling"))!.Rules!;

        static string Key(ProcessRule r) =>
            $"{r.Actions[0].TargetField}:{r.Conditions[0].Value}";

        forwardOnTheWire.Select(Key).ShouldNotBe(backwardOnTheWire.Select(Key));
        forwardOnTheWire.Count(r => r.Actions.Any(a => a.TargetField == "Custom.WayfinderAnswer"))
            .ShouldBeGreaterThan(1);

        var forward = (await BuildAssembler(forwardSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var backward = (await BuildAssembler(backwardSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        forward.ShouldNotBeNull();
        backward.ShouldNotBeNull();
        Flatten(forward).ShouldBe(Flatten(backward));

        // And the order is the SORTED one, not either input order — two runs of an unsorted
        // assembler fed the same fixture would also agree.
        var conditions = forward.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .Requiredness.Conditions;

        conditions.Count.ShouldBe(2);
        conditions.Select(c => c.Clauses[0].Value).ShouldBe(["Aborted", "Done"]);
    }

    /// <summary>
    /// Two rules imposing the SAME condition on one field are reported once.
    /// </summary>
    /// <remarks>
    /// A derived type carries ~54 rules, so duplicate conditions are reachable. Printing the
    /// same requirement twice would read like two distinct requirements on the field.
    /// </remarks>
    [Fact]
    public async Task Assemble_TwoRulesWithTheSameCondition_ReportItOnce()
    {
        ProcessRule[] rules =
        [
            MakeRequiredWhen("Custom.WayfinderAnswer", "Done"),
            MakeRequiredWhen("Custom.WayfinderAnswer", "Done"),
        ];

        var source = BuildMergeSource(rules);

        // Precondition: read back through the SEAM under test, not off the literal above — a
        // check against the literal cannot fail and would only inflate the apparent rigour.
        // Two rules must reach the assembler, both targeting the same field with the same
        // condition, or the de-duplication path never runs.
        var conditionsOnTheWire = (await source.GetTypeDetailAsync("Niflheim.Grilling"))!
            .Rules!
            .Where(r => r.Actions.Any(a => a.TargetField == "Custom.WayfinderAnswer"))
            .ToList();

        conditionsOnTheWire.Count.ShouldBe(2);
        conditionsOnTheWire.Select(r => r.Conditions.Single().Value).Distinct().Count().ShouldBe(1);

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "Custom.WayfinderAnswer")
            .Requiredness.Conditions.ShouldHaveSingleItem();
    }

    /// <summary>
    /// 🔴 An UNREADABLE rules call is labelled, not laundered into "nothing is conditionally
    /// required".
    /// </summary>
    /// <remarks>
    /// The same count-shaped-404 hazard that produced the <c>Unfetched</c> mechanism in
    /// AB#235: on this route family a failure looks exactly like a thin success. If a failed
    /// rules call rendered identically to a type with no rules, the document would make a
    /// confident claim about requiredness on the strength of a call that never came back —
    /// which is the very defect this ticket removes, reintroduced through the back door.
    /// </remarks>
    [Fact]
    public async Task Assemble_WhenTheRulesCallFailed_TheTypeIsLabelledRatherThanClaimingNoRules()
    {
        var types = new[] { "Niflheim.Grilling", "Niflheim.Decision" };
        var source = new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            // The rules call FAILED: Rules is null and the failure is named.
            ["Niflheim.Grilling"] = new(
                Fields: [new ProcessTypeField("Custom.WayfinderAnswer", "Answer", "html", null, false, "custom", false, "")],
                States: [],
                Transitions: [],
                Unfetched: ["rules"],
                Rules: null),
            // The rules call SUCCEEDED and the type genuinely has none.
            ["Niflheim.Decision"] = new(
                Fields: [new ProcessTypeField("Custom.WayfinderAnswer", "Answer", "html", null, false, "custom", false, "")],
                States: [],
                Transitions: [],
                Unfetched: null,
                Rules: []),
        });

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var couldNotRead = description.Types.Single(t => t.ReferenceName == "Niflheim.Grilling");
        var genuinelyNone = description.Types.Single(t => t.ReferenceName == "Niflheim.Decision");

        // Precondition: both report the field the same way, so the ONLY thing that can tell
        // them apart is the explicit label — which is what makes the label load-bearing.
        couldNotRead.Fields.Single().Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);
        genuinelyNone.Fields.Single().Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);

        // 🔴 Asserted by CONTAINMENT on the label under test rather than by exact list
        // equality. This fixture says nothing about value constraints, so its fields are
        // legitimately Unknown and `picklists` is legitimately present too (AB#237) — pinning
        // the whole list would make this test fail whenever an unrelated label is added, which
        // is a maintenance trap rather than a defence of the rules label.
        couldNotRead.Unfetched.ShouldContain("rules");
        genuinelyNone.Unfetched.ShouldNotContain("rules");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tests 4 and 5 — accepted values, and honest silence (AB#237)
    //
    //  🔴 THESE SHIP TOGETHER, and the spec says so in bold. Alone, test 4 ("an
    //  unconstrained field is not reported as constrained") passes against an implementation
    //  that never emits picklist data AT ALL. Test 5 — a genuinely picklist-backed field
    //  carries its resolved values — is what proves test 4 is not passing by simply never
    //  emitting anything. Deleting either one hollows out the other.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The AB#237 fixture: one field the org route reports as genuinely picklist-backed, and
    /// one it reports, explicitly, as not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Both fields are named and typed like choice lists on purpose.</b>
    /// <c>Custom.ExecutionMode</c> and <c>Custom.PriorityBand</c> are indistinguishable by
    /// name, type or shape — only the SOURCE tells them apart. A name-matching implementation
    /// would report both the same way and fail the pair, which is what makes the ban on
    /// heuristics testable rather than merely stated.
    /// </para>
    /// <para>
    /// The values are deliberately supplied in a NON-sorted order, so an assembler that
    /// carried server order through would emit them differently from one that sorted.
    /// </para>
    /// <para>
    /// 🔴 <paramref name="constraintsFailed"/> is a SEPARATE flag rather than passing
    /// <c>null</c> for <paramref name="constraints"/>. An optional parameter defaulted with
    /// <c>??</c> cannot distinguish "caller passed nothing" from "caller passed null on
    /// purpose", so the failed-call fixture would have silently received the default map and
    /// the test asserting the failure path would have been testing the success path.
    /// </para>
    /// </remarks>
    private static ScriptedDescriptionSource BuildConstraintSource(
        IReadOnlyDictionary<string, FieldValueConstraint>? constraints = null,
        bool constraintsFailed = false)
    {
        var types = new[] { "Niflheim.Decision" };

        return new ScriptedDescriptionSource(types, new Dictionary<string, ProcessTypeDetail>
        {
            ["Niflheim.Decision"] = new(
                Fields:
                [
                    // 🔴 Identical in every respect a heuristic could key on — same type, same
                    // customization, both named like enums. Only the org source distinguishes.
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
            ValueConstraints = constraintsFailed
                ? null
                : constraints ?? new Dictionary<string, FieldValueConstraint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    // Values in AUTHOR order, not sorted — the assembler must impose the order.
                    ["Custom.ExecutionMode"] = FieldValueConstraint.ConstrainedTo(
                        "WayfinderExecutionMode", ["HITL", "AFK"]),
                    // 🔴 The EXPLICIT negative: the server states this field is not list-backed.
                    ["Custom.PriorityBand"] = FieldValueConstraint.Unconstrained,
                },
        };
    }

    /// <summary>
    /// 🔴 Test 5. A genuinely picklist-backed field carries its RESOLVED values.
    /// </summary>
    /// <remarks>
    /// Without this, test 4 below is hollow: an implementation emitting no picklist data at
    /// all would satisfy "an unconstrained field is not reported as constrained" trivially.
    /// Since this org's fields are the evidence base and the fixture is synthetic, the
    /// precondition that the fixture is GENUINELY picklist-backed is asserted explicitly —
    /// otherwise the pair tests nothing twice.
    /// </remarks>
    [Fact]
    public async Task Assemble_PicklistBackedField_CarriesItsResolvedValues()
    {
        var source = BuildConstraintSource();

        // 🔴 THE PRECONDITION. Read back through the seam under test: the source must really
        // report this field as list-backed with values, or this test passes against an
        // implementation that resolves nothing.
        var fromSource = await source.GetFieldValueConstraintsAsync();
        fromSource.ShouldNotBeNull();
        fromSource["Custom.ExecutionMode"].Kind.ShouldBe(
            FieldValueConstraintKind.ListConstrained,
            "the fixture must be genuinely picklist-backed, or this test is a tautology");
        fromSource["Custom.ExecutionMode"].Values.ShouldNotBeEmpty();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var field = description.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.ExecutionMode");

        field.ValueConstraint.Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
        field.ValueConstraint.ListName.ShouldBe("WayfinderExecutionMode");

        // 🔴 The values are CARRIED — a kind flag alone would tell a caller its value must
        // come from a list without saying which, a reservation the reader cannot act on.
        // Asserted in SORTED order, not the fixture's author order, so an assembler that
        // passed server order through fails here.
        field.ValueConstraint.Values.ShouldBe(["AFK", "HITL"]);
    }

    /// <summary>
    /// 🔴 Test 4. A field that LOOKS like a choice list but is not picklist-backed is reported
    /// as unconstrained — as a fact read off the server, never as a guess.
    /// </summary>
    /// <remarks>
    /// The honesty constraint (b) proper, and the mirror of AB#236: where that ticket was
    /// about a document that UNDERSTATED requiredness, this is about one that could OVERSTATE
    /// constraint. The fixture's two fields are indistinguishable by name and type, so an
    /// implementation guessing from either reports them alike and fails this test or its pair.
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldThatIsNotPicklistBacked_IsReportedAsUnconstrained()
    {
        var source = BuildConstraintSource();

        // 🔴 THE PRECONDITION for the no-heuristic claim: the two fields really are alike in
        // every attribute a name- or type-matching implementation could key on. Without this
        // the test could pass merely because the two happened to differ somewhere else.
        var detail = await source.GetTypeDetailAsync("Niflheim.Decision");
        var constrained = detail!.Fields.Single(f => f.ReferenceName == "Custom.ExecutionMode");
        var unconstrained = detail.Fields.Single(f => f.ReferenceName == "Custom.PriorityBand");
        constrained.Type.ShouldBe(unconstrained.Type);
        constrained.Customization.ShouldBe(unconstrained.Customization);

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var field = description.Types[0].Fields
            .Single(f => f.ReferenceName == "Custom.PriorityBand");

        field.ValueConstraint.Kind.ShouldBe(
            FieldValueConstraintKind.Unconstrained,
            "the server states this field is not list-backed — reporting it as constrained "
            + "would tell a caller its value must come from a list the server does not enforce");
        field.ValueConstraint.Values.ShouldBeEmpty();
        field.ValueConstraint.ListName.ShouldBeNull();

        // 🔴 And the OTHER field, in the same document, is constrained. This is what stops
        // this test passing against an implementation that reports every field unconstrained
        // — the hollow-guard failure the spec names for exactly this pair.
        description.Types[0].Fields.Single(f => f.ReferenceName == "Custom.ExecutionMode")
            .ValueConstraint.Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
    }

    /// <summary>
    /// 🔴 An UNREADABLE picklist source is labelled, not laundered into "unconstrained".
    /// </summary>
    /// <remarks>
    /// The count-shaped-404 hazard again. "We could not read the lists" and "the server
    /// accepts anything here" are different claims and only one is safe to act on — and the
    /// second is this ticket's own lie, arriving through a failed fetch instead of a bad
    /// guess. So a failed constraint call yields <c>unknown</c> per field AND puts
    /// <c>picklists</c> in the type's unfetched list.
    /// </remarks>
    [Fact]
    public async Task Assemble_WhenThePicklistCallFailed_FieldsAreUnknownAndTheTypeIsLabelled()
    {
        // 🔴 constraintsFailed, not `constraints: null`: the call FAILED. An empty or default
        // map would mean the source was read and reported something, which is a different
        // fact — and an optional parameter cannot express "null on purpose".
        var source = BuildConstraintSource(constraintsFailed: true);
        source.ValueConstraints.ShouldBeNull();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var type = description.Types[0];

        foreach (var field in type.Fields)
        {
            field.ValueConstraint.Kind.ShouldBe(
                FieldValueConstraintKind.Unknown,
                "a failed picklist call must not render as 'the server accepts anything'");
        }

        // 🔴 …and the failure is NAMED. Without the label, `unknown` on every field is the
        // only signal, and a reader diffing two documents would see a clean diff where one
        // side simply failed to ask.
        type.Unfetched.ShouldContain("picklists");
    }

    /// <summary>
    /// A field the org source does not report at all is <c>unknown</c>, not unconstrained.
    /// </summary>
    /// <remarks>
    /// The map is org-scoped and the field list is type-scoped, so a row present on one and
    /// absent from the other is a source disagreement. Inventing "unconstrained" for it would
    /// be a confident claim nothing supports — and it is the same overstatement the ticket
    /// removes, reached by a different route.
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldMissingFromTheConstraintSource_IsUnknownRatherThanUnconstrained()
    {
        // The source answers about one of the two fields only.
        var source = BuildConstraintSource(new Dictionary<string, FieldValueConstraint>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.ExecutionMode"] = FieldValueConstraint.Unconstrained,
        });

        // Precondition: the map is non-null (the call SUCCEEDED) and genuinely omits the other
        // field — so this is about a partial answer, not a failed one.
        var fromSource = await source.GetFieldValueConstraintsAsync();
        fromSource.ShouldNotBeNull();
        fromSource.ShouldNotContainKey("Custom.PriorityBand");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types[0].Fields.Single(f => f.ReferenceName == "Custom.PriorityBand")
            .ValueConstraint.Kind.ShouldBe(FieldValueConstraintKind.Unknown);

        // 🔴 The type IS labelled, even though the source call SUCCEEDED. A partial failure is
        // still a failure: without the label the document would say `unknown` on this field
        // while its unfetched list said everything was read — claiming "I could not read this"
        // and "everything was read" at once. The label is derived from the resolved answers,
        // not from whether the call came back.
        description.Types[0].Unfetched.ShouldContain("picklists");
    }

    /// <summary>
    /// 🔴 A PARTIAL picklist failure is labelled too, not only a total one.
    /// </summary>
    /// <remarks>
    /// The defect this reds against: labelling only on a null map lets the document emit
    /// <c>valueConstraint: unknown</c> for a field while that type's <c>unfetched</c> list is
    /// EMPTY — simultaneously claiming "I could not read this" and "everything was read".
    /// A reader diffing two documents sees a clean unfetched column and has no signal that any
    /// constraint answer is missing, which is this ticket's own failure mode arriving one
    /// layer down. The label is therefore derived from the RESOLVED answers rather than from
    /// whether the call came back.
    /// </remarks>
    [Fact]
    public async Task Assemble_WhenOnlySomeConstraintsResolved_TheTypeIsStillLabelled()
    {
        // The call SUCCEEDED — one field resolved, the other came back Unknown.
        var source = BuildConstraintSource(new Dictionary<string, FieldValueConstraint>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.ExecutionMode"] = FieldValueConstraint.ConstrainedTo("L", ["A"]),
            ["Custom.PriorityBand"] = FieldValueConstraint.Unknown,
        });

        // Precondition: the map is NON-null, so this is a partial failure and not the total
        // one the other test covers. Without this the two tests would be the same test.
        var fromSource = await source.GetFieldValueConstraintsAsync();
        fromSource.ShouldNotBeNull();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var type = description.Types[0];

        // Precondition: one field really did resolve, so this is not the total-failure shape
        // wearing a different hat.
        type.Fields.Single(f => f.ReferenceName == "Custom.ExecutionMode")
            .ValueConstraint.Kind.ShouldBe(FieldValueConstraintKind.ListConstrained);
        type.Fields.Single(f => f.ReferenceName == "Custom.PriorityBand")
            .ValueConstraint.Kind.ShouldBe(FieldValueConstraintKind.Unknown);

        type.Unfetched.ShouldContain(
            "picklists",
            "an unlabelled Unknown claims 'I could not read this' and 'everything was read' "
            + "at the same time");
    }

    /// <summary>
    /// …and a type whose constraints ALL resolved is not labelled.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side. Without this, an implementation that labelled every type
    /// unconditionally would pass every assertion above while making the label meaningless —
    /// a false reservation is as much a lie as a missing one.
    /// </remarks>
    [Fact]
    public async Task Assemble_WhenEveryConstraintResolved_TheTypeIsNotLabelled()
    {
        var source = BuildConstraintSource();

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        description.ShouldNotBeNull();

        var type = description.Types[0];

        // Precondition: nothing is Unknown, so the label genuinely has no reason to fire.
        type.Fields.ShouldAllBe(f => f.ValueConstraint.Kind != FieldValueConstraintKind.Unknown);

        type.Unfetched.ShouldNotContain("picklists");
    }

    /// <summary>
    /// 🔴 The constraint join is case-INSENSITIVE on the field reference name.
    /// </summary>
    /// <remarks>
    /// The org field route and the per-type fields route are different surfaces, and this
    /// route family is already known to be inconsistent about spelling. An ordinal-exact join
    /// would drop a real constraint over a casing difference and report a list-backed field as
    /// unconstrained — byte-identical to a field that genuinely is, and with no unfetched
    /// label to catch it. Independent review caught exactly this class of defect in AB#236.
    /// </remarks>
    [Fact]
    public async Task Assemble_ConstraintKeyedWithDifferentCasing_StillJoinsToTheField()
    {
        var source = BuildConstraintSource(new Dictionary<string, FieldValueConstraint>(
            StringComparer.OrdinalIgnoreCase)
        {
            // Deliberately spelled differently from the fields route's `Custom.ExecutionMode`.
            ["custom.executionmode"] = FieldValueConstraint.ConstrainedTo("List", ["B", "A"]),
        });

        // Precondition: the two spellings really do differ, or the join is not exercised.
        var detail = await source.GetTypeDetailAsync("Niflheim.Decision");
        var fieldsSpelling = detail!.Fields
            .Single(f => string.Equals(f.ReferenceName, "Custom.ExecutionMode", StringComparison.OrdinalIgnoreCase))
            .ReferenceName;
        fieldsSpelling.ShouldNotBe(
            "custom.executionmode",
            "the two routes must genuinely spell the reference name differently, or the "
            + "case-insensitive join is not exercised");

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        var field = description!.Types[0].Fields.Single(f => f.ReferenceName == fieldsSpelling);
        field.ValueConstraint.Kind.ShouldBe(
            FieldValueConstraintKind.ListConstrained,
            "a casing difference between two routes must not silently drop a real constraint");
        field.ValueConstraint.Values.ShouldBe(["A", "B"]);
    }

    /// <summary>
    /// 🔴 Two fields differing ONLY in their value constraint order deterministically.
    /// </summary>
    /// <remarks>
    /// The field sort's tiebreak chain must stay TOTAL over every document-visible member.
    /// Adding <c>ValueConstraint</c> without extending the chain would leave two rows agreeing
    /// on reference name, display name and type but constrained to different lists falling
    /// through <c>OrderBy</c>'s stability to WIRE order — reintroducing the exact
    /// non-determinism the assembler exists to remove, and silently. Independent review caught
    /// AB#236 adding a member without extending the chain; this test reds against repeating it.
    /// <para>
    /// Modelled on <c>Assemble_FieldsDifferingOnlyInRequiredness_AreOrderedDeterministically</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_FieldsDifferingOnlyInValueConstraint_AreOrderedDeterministically()
    {
        // Two rows identical in EVERY other document-visible member. Only the constraint can
        // break the tie.
        ProcessTypeField Row() => new(
            "Custom.Duplicate", "Duplicate", "string", null, false, "custom", false, "");

        var types = new[] { "Niflheim.Decision" };

        ScriptedDescriptionSource Build(IReadOnlyList<string> values) =>
            new(types, new Dictionary<string, ProcessTypeDetail>
            {
                ["Niflheim.Decision"] = new(
                    Fields: [Row(), Row()],
                    States: [],
                    Transitions: [],
                    Unfetched: null,
                    Rules: []),
            })
            {
                ValueConstraints = new Dictionary<string, FieldValueConstraint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["Custom.Duplicate"] = FieldValueConstraint.ConstrainedTo("L", values),
                },
            };

        // The SAME constraint arriving with its values in two different server orders must
        // produce the identical document — the values are sorted, so the rows cannot swap.
        var first = (await BuildAssembler(Build(["Zulu", "Alpha"])).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var second = (await BuildAssembler(Build(["Alpha", "Zulu"])).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        Flatten(first).ShouldBe(Flatten(second));

        // Precondition: there really are two otherwise-identical rows, so the tiebreak chain
        // is genuinely exercised rather than the test comparing a single row to itself.
        first.Types[0].Fields.Count.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 6 — inherited rules are PRESENT, each tagged (AB#238)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A type carrying many inherited rules emits ALL of them, each tagged with its
    /// customization type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Spec test 6, and the reversal most likely to be "helpfully" undone.</b> The volume
    /// argument for filtering is real — verified live, <c>Niflheim.Epic</c> carries 54 rules of
    /// which 53 are system plumbing and 1 was authored — and it was ruled against anyway,
    /// because a difference that exists only in the omitted part diffs clean.
    /// </para>
    /// <para>
    /// 🔴 <b>The assertion is a COUNT, not merely non-empty</b>, exactly as the spec requires:
    /// an implementation that filtered the inherited rules out would emit the 1 authored rule
    /// and pass a non-empty check while having undone the ruling completely. The fixture is
    /// built at the live ratio so the count means what it appears to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assemble_InheritedRules_ArePresentAndTagged()
    {
        const string Type = "Niflheim.Epic";
        const int SystemRules = 53;

        // The live shape at the live ratio: 53 system rules, 1 authored.
        var rules = new List<ProcessRule>();
        for (var i = 0; i < SystemRules; i++)
        {
            rules.Add(new ProcessRule(
                Conditions: [new RuleCondition("when", "System.State", $"S{i:D2}")],
                Actions: [new RuleAction("makeReadOnly", $"System.Field{i:D2}", null)],
                IsDisabled: false,
                Customization: RuleCustomization.From("system")));
        }

        rules.Add(new ProcessRule(
            Conditions: [new RuleCondition("when", "System.State", "Done")],
            Actions: [new RuleAction("makeRequired", "Custom.ClosingStatement", null)],
            IsDisabled: false,
            Customization: RuleCustomization.From("custom"),
            Name: "Epic must state what it delivered"));

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(Fields: [], States: [], Transitions: [], Unfetched: null, Rules: rules),
        });

        // Precondition, asserted rather than assumed: the fixture really does carry the
        // lopsided ratio the ruling is about. If it carried only authored rules, an
        // implementation that filtered would pass every assertion below.
        rules.Count(r => r.CustomizationOrUnknown.Kind == RuleCustomizationKind.System)
            .ShouldBe(SystemRules);
        rules.Count(r => r.CustomizationOrUnknown.Kind == RuleCustomizationKind.Custom).ShouldBe(1);

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var described = description!.Types.Single();

        // 🔴 A COUNT, not non-empty. This is the assertion a filtering implementation fails.
        described.Rules.Count.ShouldBe(
            SystemRules + 1,
            "every rule is carried, inherited plumbing included — a reader who wants only the "
            + "authored ones can filter a complete document, but a reader handed a filtered "
            + "one cannot recover what was dropped or tell that anything was");

        described.Rules.Count(r => r.Customization.Kind == RuleCustomizationKind.System)
            .ShouldBe(SystemRules);

        // 🔴 …and every one carries its tag, which is what makes the filtering available to
        // the READER. Rules carried without the tag would pay the noise cost and deliver none
        // of the mitigation the ruling relies on.
        described.Rules.ShouldAllBe(r => r.Customization.Kind != RuleCustomizationKind.Unknown);

        var authored = described.Rules.Single(
            r => r.Customization.Kind == RuleCustomizationKind.Custom);
        authored.Name.ShouldBe("Epic must state what it delivered");
        authored.Actions.Single().TargetField.ShouldBe("Custom.ClosingStatement");
    }

    /// <summary>
    /// A rule whose <c>customizationType</c> the server did not send is reported as
    /// <c>Unknown</c>, never as <c>System</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the tag. Reading an absent key as <c>system</c> is the reading a
    /// nullable-to-default deserialization invites, and it is dangerous in a specific way: the
    /// tag is the reader's FILTER, so mislabelling an authored rule as inherited plumbing
    /// invites the reader to throw it away — undoing the carry-everything ruling from the far
    /// end while the document still technically carries everything.
    /// </remarks>
    [Fact]
    public async Task Assemble_RuleWithNoCustomizationType_IsUnknownNotSystem()
    {
        const string Type = "Niflheim.Grilling";

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(
                Fields: [], States: [], Transitions: [], Unfetched: null,
                Rules:
                [
                    // No Customization supplied at all — the "server did not say" case.
                    new ProcessRule(
                        Conditions: [new RuleCondition("when", "System.State", "Done")],
                        Actions: [new RuleAction("makeRequired", "Custom.Answer", null)],
                        IsDisabled: false),
                ]),
        });

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types.Single().Rules.Single().Customization.Kind.ShouldBe(
            RuleCustomizationKind.Unknown,
            "an absent customizationType is not the server stating a class — reporting it as "
            + "'system' would invite a reader to filter away a rule that may be authored");
    }

    /// <summary>A DISABLED rule is carried, with its flag, rather than dropped.</summary>
    /// <remarks>
    /// 🔴 Not in tension with the requiredness merge skipping disabled rules. There, a disabled
    /// rule must not make a field read as required, because it does not fire. Here, a rule
    /// disabled on one process and enabled on another is a real structural difference and
    /// dropping it would diff clean over exactly that.
    /// </remarks>
    [Fact]
    public async Task Assemble_DisabledRule_IsCarriedWithItsFlag()
    {
        const string Type = "Niflheim.Grilling";

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(
                Fields:
                [
                    new ProcessTypeField(
                        "Custom.Answer", "Answer", "string", null, false, "custom", false, ""),
                ],
                States: [], Transitions: [], Unfetched: null,
                Rules:
                [
                    new ProcessRule(
                        Conditions: [new RuleCondition("when", "System.State", "Done")],
                        Actions: [new RuleAction("makeRequired", "Custom.Answer", null)],
                        IsDisabled: true,
                        Customization: RuleCustomization.From("custom"),
                        Name: "A disabled rule"),
                ]),
        });

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var described = description!.Types.Single();

        described.Rules.Count.ShouldBe(1);
        described.Rules.Single().IsDisabled.ShouldBeTrue();

        // …and the disabled rule still does not make the field required, which is the AB#236
        // behaviour this must not disturb.
        described.Fields.Single().Requiredness.Kind.ShouldBe(FieldRequirednessKind.Never);
    }

    /// <summary>
    /// Two rules differing only in a member below the identifying ones order
    /// deterministically rather than by wire order.
    /// </summary>
    /// <remarks>
    /// 🔴 The rules analogue of
    /// <c>Assemble_FieldsDifferingOnlyInValueConstraint_AreOrderedDeterministically</c>.
    /// Independent review caught both AB#236 and AB#237 adding a document member without
    /// extending a sort chain; this is the guard for the one this ticket adds. Two rules
    /// alike on customization, name and disabled-flag but differing in their CONDITIONS would
    /// otherwise fall through <c>OrderBy</c>'s stability to the order the server sent them.
    /// </remarks>
    [Fact]
    public async Task Assemble_RulesDifferingOnlyInConditions_AreOrderedDeterministically()
    {
        const string Type = "Niflheim.Grilling";

        ProcessRule Rule(string state) => new(
            Conditions: [new RuleCondition("when", "System.State", state)],
            Actions: [new RuleAction("makeRequired", "Custom.Answer", null)],
            IsDisabled: false,
            Customization: RuleCustomization.From("custom"),
            Name: "Same name");

        ProcessTypeDetail Detail(bool doneFirst) => new(
            Fields: [], States: [], Transitions: [], Unfetched: null,
            Rules: doneFirst
                ? [Rule("Done"), Rule("Doing")]
                : [Rule("Doing"), Rule("Done")]);

        var forwardSource = new ScriptedDescriptionSource([Type],
            new Dictionary<string, ProcessTypeDetail> { [Type] = Detail(true) });
        var reversedSource = new ScriptedDescriptionSource([Type],
            new Dictionary<string, ProcessTypeDetail> { [Type] = Detail(false) });

        // Precondition: the fixtures really do present the two rules in opposite WIRE order,
        // and the rules really are identical on every member above the conditions — otherwise
        // an earlier tiebreak resolves it and the new one is never exercised.
        var forwardWire = (await forwardSource.GetTypeDetailAsync(Type))!.Rules!;
        var reversedWire = (await reversedSource.GetTypeDetailAsync(Type))!.Rules!;
        forwardWire.Select(r => r.Conditions[0].Value)
            .ShouldBe(reversedWire.Select(r => r.Conditions[0].Value).Reverse());
        forwardWire.Select(r => (r.Name, r.IsDisabled, r.CustomizationOrUnknown.Kind))
            .Distinct().Count().ShouldBe(1);

        var forward = (await BuildAssembler(forwardSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var reversed = (await BuildAssembler(reversedSource).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        Flatten(forward!).ShouldBe(
            Flatten(reversed!),
            "two rules differing only below the identifying members must not order by wire "
            + "order");

        // 🔴 The positive half. Stability alone is satisfied by an assembler that emits ZERO
        // rules — two empty documents flatten identically — so the resulting ORDER is pinned
        // explicitly rather than only compared to itself.
        var described = forward!.Types.Single().Rules;
        described.Count.ShouldBe(2, "an assembler emitting no rules would pass the stability "
            + "assertion above by vacuity");
        described.Select(r => r.Conditions.Single().Value).ShouldBe(
            ["Done", "Doing"],
            "the tiebreak is the LENGTH-PREFIXED canonical clause key, so \"4:Done\" precedes "
            + "\"5:Doing\" — deliberately not a plain lexicographic compare on the value, "
            + "because a rule condition's value is an arbitrary user string in which no "
            + "separator can be assumed absent");
    }

    /// <summary>
    /// A rule's own conditions and actions are ordered too, not just the rules themselves.
    /// </summary>
    /// <remarks>
    /// 🔴 The level below the one an implementer naturally guards. Sorting the rule LIST while
    /// carrying each rule's clauses in server order leaves byte-stability broken inside every
    /// multi-clause rule — invisible to a test that only compares rule counts or names.
    /// </remarks>
    [Fact]
    public async Task Assemble_RuleClausesAndActions_AreOrderedNotCarriedInWireOrder()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var multiClause = description!.Types
            .Single(t => t.ReferenceName == TypeWithRules)
            .Rules.Single(r => r.Conditions.Count > 1);

        // The fixture presents these as whenWas-then-when and makeReadOnly-then-copyValue.
        multiClause.Conditions.Select(c => c.ConditionType).ShouldBe(["when", "whenWas"]);
        multiClause.Actions.Select(a => a.ActionType).ShouldBe(["copyValue", "makeReadOnly"]);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Behaviour membership (AB#238)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Which backlog levels a type belongs to appears, named from the process catalogue.
    /// </summary>
    /// <remarks>
    /// 🔴 The NAME is the point of the join. The membership route returns a bare reference, and
    /// a custom backlog level's reference name is a GUID — so a document carrying the edge
    /// alone would be true, unreadable, and worthless in a diff between two processes whose
    /// levels have different ids and the same name.
    /// </remarks>
    [Fact]
    public async Task Assemble_BehaviourMembership_AppearsWithNamesFromTheCatalogue()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var type = description!.Types.Single(t => t.ReferenceName == TypeWithRules);

        type.Behaviours.Count.ShouldBe(2);

        // 🔴 Ordered by REFERENCE name, not by rank. The catalogue ranks Zeta(10) below
        // Alpha(40), so a rank ordering would put Zeta first — this assertion is what
        // distinguishes the two.
        type.Behaviours.Select(b => b.ReferenceName).ShouldBe(["Custom.Alpha", "Custom.Zeta"]);
        type.Behaviours.Select(b => b.Name).ShouldBe(["Alpha Backlog", "Zeta Backlog"]);
        type.Behaviours.Select(b => b.Rank).ShouldBe([40, 10]);

        // The default level is carried: where a new item of this type lands is a real
        // difference between two processes.
        type.Behaviours.Single(b => b.IsDefault).ReferenceName.ShouldBe("Custom.Alpha");

        // Nothing is unfetched — every membership was named.
        type.Unfetched.ShouldNotContain("behaviourCatalogue");
    }

    /// <summary>
    /// The catalogue is fetched ONCE per run, not once per type.
    /// </summary>
    /// <remarks>
    /// Not a call-count assertion for its own sake — the spec forbids pinning the fetch layer's
    /// shape. This guards the cost model: the catalogue is process-scoped, so asking per type
    /// would multiply round-trips by the type count for an identical answer, on a command whose
    /// accepted latency ceiling is already round-trip-bound.
    /// </remarks>
    [Fact]
    public async Task Assemble_BehaviourCatalogue_IsFetchedOncePerRunNotPerType()
    {
        var source = BuildSource();

        await BuildAssembler(source).AssembleAsync(null, FixedCapture);

        source.RequestedTypes.Count.ShouldBe(3, "precondition: three types were described");
        source.BehaviourCatalogueCallCount.ShouldBe(1);
    }

    /// <summary>
    /// A membership whose catalogue entry is missing keeps its reference name and is LABELLED,
    /// rather than being dropped or silently unnamed.
    /// </summary>
    /// <remarks>
    /// 🔴 Two failure modes rejected at once. Dropping the membership would let a real
    /// difference — this type is on a backlog level, that one is not — diff clean, which is the
    /// omission this feature exists to prevent. Keeping it unnamed and unlabelled would show a
    /// reader a blank name with no explanation while the type's unfetched list claimed
    /// everything was read.
    /// </remarks>
    [Fact]
    public async Task Assemble_UnresolvedBehaviour_KeepsItsReferenceAndIsLabelled()
    {
        var source = new ScriptedDescriptionSource(
            [TypeWithRules],
            new Dictionary<string, ProcessTypeDetail> { [TypeWithRules] = HostileDetail("x") })
        {
            // Read successfully, but names neither of the memberships.
            BehaviourCatalogue = [],
        };

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var type = description!.Types.Single();

        type.Behaviours.Count.ShouldBe(
            2,
            "an unnamed membership is still a membership — dropping it would let a real "
            + "difference diff clean");
        type.Behaviours.ShouldAllBe(b => b.Name == string.Empty);
        type.Unfetched.ShouldContain(
            "behaviourCatalogue",
            "a blank name with no label would read as a level that has no name, rather than "
            + "one this document could not name");
    }

    /// <summary>
    /// A FAILED catalogue call labels the type, while an EMPTY-but-read catalogue that names
    /// everything does not.
    /// </summary>
    /// <remarks>
    /// 🔴 The distinction the label exists for. <c>null</c> means "we could not ask" and an
    /// empty list means "we asked and it named nothing" — different facts, and only the first
    /// justifies a reservation on a type whose memberships were all resolvable.
    /// </remarks>
    [Fact]
    public async Task Assemble_FailedCatalogue_LabelsOnlyTypesThatHaveMemberships()
    {
        const string WithMemberships = "Niflheim.CustomAlpha";
        const string WithoutMemberships = "Niflheim.CustomBeta";

        var source = new ScriptedDescriptionSource(
            [WithMemberships, WithoutMemberships],
            new Dictionary<string, ProcessTypeDetail>
            {
                [WithMemberships] = HostileDetail("a"),
                [WithoutMemberships] = new(
                    Fields: [], States: [], Transitions: [], Unfetched: null,
                    Rules: [], Behaviours: []),
            })
        {
            // The call FAILED — not merely returned nothing.
            BehaviourCatalogue = null,
        };

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        description!.Types.Single(t => t.ReferenceName == WithMemberships)
            .Unfetched.ShouldContain("behaviourCatalogue");

        // 🔴 …and the type with no memberships is NOT labelled. It lost nothing when the
        // catalogue failed, and a false reservation is as much a lie as a missing one.
        description.Types.Single(t => t.ReferenceName == WithoutMemberships)
            .Unfetched.ShouldNotContain(
                "behaviourCatalogue",
                "a type with no memberships lost nothing to the failed catalogue — declaring "
                + "a reservation would be a false one");
    }

    /// <summary>
    /// The catalogue join is case-INSENSITIVE, like every cross-route name match in this layer.
    /// </summary>
    /// <remarks>
    /// 🔴 Independent review caught AB#236 getting this wrong on a different join. An exact
    /// join here would silently drop a behaviour's NAME over a casing difference between two
    /// routes, leaving the document asserting membership of an unnamed GUID and putting a
    /// reservation on a type that did not need one.
    /// </remarks>
    [Fact]
    public async Task Assemble_BehaviourJoin_IsCaseInsensitive()
    {
        const string Type = "Niflheim.Grilling";

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(
                Fields: [], States: [], Transitions: [], Unfetched: null, Rules: [],
                Behaviours:
                [
                    new ProcessBehaviourMembership(
                        "custom.3DAA3B35", string.Empty, null, IsDefault: true),
                ]),
        })
        {
            // Same identity, different spelling — which is what the two routes may do.
            BehaviourCatalogue = [new ProcessBehaviourSummary("Custom.3daa3b35", "Wayfinding", 40)],
        };

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var membership = description!.Types.Single().Behaviours.Single();

        membership.Name.ShouldBe(
            "Wayfinding",
            "an ordinal-exact join would drop a real name over a casing difference between "
            + "two routes");
        membership.Rank.ShouldBe(40);
        description.Types.Single().Unfetched.ShouldNotContain("behaviourCatalogue");

        // 🔴 The emitted reference name is the CATALOGUE's spelling, not the membership
        // route's. The join is case-insensitive precisely because the two routes are not known
        // to agree on casing — so carrying the membership route's spelling would put an
        // unstable value in the document AND in the ordinal sort key that positions the row,
        // which is the diff noise the case-insensitive join exists to prevent.
        membership.ReferenceName.ShouldBe(
            "Custom.3daa3b35",
            "the catalogue is the naming authority; emitting the membership route's spelling "
            + "would make two documents differ over a casing difference");
    }

    /// <summary>
    /// Two catalogue rows for one behaviour that DISAGREE leave it unnamed and labelled, rather
    /// than resolving by wire order.
    /// </summary>
    /// <remarks>
    /// 🔴 Keeping the first row and keeping the last are BOTH order-dependent — only an
    /// order-independent rule is not. The index is <c>OrdinalIgnoreCase</c>, so two rows
    /// differing only in the casing of <c>referenceName</c> collide here, which is the
    /// realistic case. This mirrors AB#237's rule for two disagreeing field rows exactly: when
    /// neither answer is defensible, the honest report is that we do not know.
    /// </remarks>
    [Fact]
    public async Task Assemble_CatalogueRowsThatDisagree_LeaveTheBehaviourUnnamedAndLabelled()
    {
        const string Type = "Niflheim.Grilling";

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(
                Fields: [], States: [], Transitions: [], Unfetched: null, Rules: [],
                Behaviours: [new ProcessBehaviourMembership("Custom.X", string.Empty, null, true)]),
        })
        {
            // Same identity by the index's comparer, different answers.
            BehaviourCatalogue =
            [
                new ProcessBehaviourSummary("Custom.X", "One Name", 10),
                new ProcessBehaviourSummary("custom.x", "Another Name", 20),
            ],
        };

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var type = description!.Types.Single();

        var membership = type.Behaviours.Single();
        membership.Name.ShouldBe(
            string.Empty,
            "picking either row would make the document depend on the order the server sent "
            + "them; neither name is defensible");
        membership.Rank.ShouldBeNull();
        type.Unfetched.ShouldContain(
            "behaviourCatalogue",
            "a blank name with no label reads as a level that has no name, rather than one "
            + "this document could not name");
    }

    /// <summary>
    /// Two catalogue rows that AGREE are not treated as a conflict.
    /// </summary>
    /// <remarks>
    /// 🔴 The other side of the guard. Without it, an implementation that degraded on ANY
    /// duplicate would pass the test above while discarding a perfectly good name — the
    /// hollow-guard shape this repo has been bitten by.
    /// </remarks>
    [Fact]
    public async Task Assemble_CatalogueRowsThatAgree_KeepTheName()
    {
        const string Type = "Niflheim.Grilling";

        var source = new ScriptedDescriptionSource([Type], new Dictionary<string, ProcessTypeDetail>
        {
            [Type] = new(
                Fields: [], States: [], Transitions: [], Unfetched: null, Rules: [],
                Behaviours: [new ProcessBehaviourMembership("Custom.X", string.Empty, null, true)]),
        })
        {
            BehaviourCatalogue =
            [
                new ProcessBehaviourSummary("Custom.X", "Same Name", 10),
                new ProcessBehaviourSummary("custom.x", "Same Name", 10),
            ],
        };

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var type = description!.Types.Single();

        type.Behaviours.Single().Name.ShouldBe(
            "Same Name",
            "two rows carrying the same answer are not a disagreement — degrading here would "
            + "discard a good name over a duplicate that agrees");
        type.Unfetched.ShouldNotContain("behaviourCatalogue");
    }

    /// <summary>
    /// Two membership rows for one behaviour that disagree on <c>isDefault</c> resolve to the
    /// WEAKER claim, not to whichever the server sent first.
    /// </summary>
    /// <remarks>
    /// 🔴 <c>IsDefault</c> is output-visible and answers "where does a new item of this type
    /// land". Collapsing two rows by keeping the first makes that answer depend on wire order —
    /// and the sort chain cannot rescue it, because the losing row is already discarded. The
    /// weaker claim wins, matching AB#237's rule for two witnesses that disagree.
    /// </remarks>
    [Fact]
    public async Task Assemble_DuplicateMembershipsDisagreeingOnDefault_ResolveToTheWeakerClaim()
    {
        const string Type = "Niflheim.Grilling";

        ProcessTypeDetail Detail(bool defaultFirst) => new(
            Fields: [], States: [], Transitions: [], Unfetched: null, Rules: [],
            Behaviours: defaultFirst
                ? [
                    new ProcessBehaviourMembership("Custom.X", string.Empty, null, true),
                    new ProcessBehaviourMembership("custom.x", string.Empty, null, false),
                ]
                : [
                    new ProcessBehaviourMembership("custom.x", string.Empty, null, false),
                    new ProcessBehaviourMembership("Custom.X", string.Empty, null, true),
                ]);

        ScriptedDescriptionSource Source(bool defaultFirst) =>
            new([Type], new Dictionary<string, ProcessTypeDetail> { [Type] = Detail(defaultFirst) })
            {
                BehaviourCatalogue = [new ProcessBehaviourSummary("Custom.X", "X", 10)],
            };

        var forward = (await BuildAssembler(Source(true)).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var reversed = (await BuildAssembler(Source(false)).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        // The document must not depend on which row the server sent first.
        Flatten(forward!).ShouldBe(
            Flatten(reversed!),
            "collapsing two membership rows by keeping the first makes isDefault depend on "
            + "wire order");

        var membership = forward!.Types.Single().Behaviours.Single();
        membership.IsDefault.ShouldBeFalse(
            "on disagreement the weaker claim wins — asserting the stronger one on the "
            + "strength of a contradiction is not defensible");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Form layout (AB#238)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The form layout appears, ordered on the server's <c>order</c> key rather than
    /// alphabetically or in wire order.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This is the one collection whose ORDER IS ITS CONTENT.</b> "Description sits above
    /// Title" is precisely the fact a reader asked for, so an alphabetical sort would be both
    /// deterministic and wrong. The fixture presents every level with its order key DESCENDING,
    /// so an implementation that trusted array order emits the form upside down and this test
    /// sees it.
    /// </remarks>
    [Fact]
    public async Task Assemble_FormLayout_IsOrderedByTheServersOrderKey()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var layout = description!.Types
            .Single(t => t.ReferenceName == TypeWithRules)
            .Layout;

        layout.ShouldNotBeNull();

        // Pages by their order key: the fixture supplies Second(1) before First(0).
        layout.Pages.Select(p => p.Id).ShouldBe(["Page.First", "Page.Second"]);

        var first = layout.Pages[0];

        // Sections by id — the server gives them no order key, and the id IS the arrangement.
        first.Sections.Select(s => s.Id).ShouldBe(["Section1", "Section2"]);

        // Groups by order key, per column.
        first.Sections[0].Groups.Select(g => g.Id).ShouldBe(["Group.Early"]);
        first.Sections[1].Groups.Select(g => g.Id).ShouldBe(["Group.Late"]);

        // 🔴 Controls by ORDER KEY, and the fixture is built so alphabetical disagrees:
        // System.Title is order 0 and Custom.Zulu is order 1, so an implementation sorting
        // the layout by id would emit Custom.Zulu first. Alphabetical would be deterministic
        // and WRONG — a form's arrangement is the content the reader asked for.
        first.Sections[1].Groups[0].Controls.Select(c => c.Id)
            .ShouldBe(
                ["System.Title", "Custom.Zulu"],
                "the layout is ordered on the server's arrangement key, not alphabetically — "
                + "sorting a form by control name destroys the fact it exists to carry");
        first.Sections[1].Groups[0].Controls.Select(c => c.Order).ShouldBe([0, 1]);

        // The control type is carried verbatim — the reader compares the server's vocabulary.
        first.Sections[0].Groups[0].Controls.Single().ControlType.ShouldBe("HtmlFieldControl");

        // 🔴 Inherited-vs-authored is marked on the layout too, the same distinction rules and
        // types carry.
        first.Sections[0].Groups[0].Controls.Single().Inherited.ShouldBeTrue();
        first.Inherited.ShouldBeFalse();

        // 🔴 The SYSTEM controls — state, area path and the rest the server places outside the
        // page structure — are carried and ordered on the same key. They arrive in the SAME
        // response as the pages, so dropping them would be an omission with no marker; an
        // earlier draft deserialized and discarded them while the header claimed the document
        // made no reservations.
        layout.SystemControls.Select(c => c.Id).ShouldBe(
            ["System.State", "System.AreaPath"],
            "system controls are reachable in the same payload, so the carry-everything ruling "
            + "reaches them too — and they order on the server's key like any other control");
    }

    /// <summary>
    /// A layout that could NOT be read is <c>null</c> and labelled, never an empty layout.
    /// </summary>
    /// <remarks>
    /// 🔴 An empty layout is the strongest possible positive claim — "this type's form has no
    /// pages at all" — and nothing observed serves one. Rendering a failed fetch as one would
    /// be worse than the silent omission it replaced.
    /// </remarks>
    [Fact]
    public async Task Assemble_UnfetchedLayout_IsNullAndLabelledNotAnEmptyLayout()
    {
        const string Type = "Niflheim.Grilling";
        const string TypeThatHasALayout = "Niflheim.Zulu";

        var source = new ScriptedDescriptionSource(
            [Type, TypeThatHasALayout],
            new Dictionary<string, ProcessTypeDetail>
            {
                [Type] = new(
                    Fields: [], States: [], Transitions: [],
                    Unfetched: ["formLayout"],
                    Rules: [], Behaviours: [], Layout: null),
                // The discriminating counterpart: a layout that WAS read.
                [TypeThatHasALayout] = HostileDetail("z"),
            });

        var description = (await BuildAssembler(source).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();
        var type = description!.Types.Single(t => t.ReferenceName == Type);

        type.Layout.ShouldBeNull();
        type.Unfetched.ShouldContain("formLayout");

        // 🔴 The positive counterpart, in the SAME test. Without it this asserts only two
        // pass-throughs the assembler performs unconditionally, and would pass against an
        // assembler with no layout handling whatsoever — Layout would default to null and the
        // unfetched list is copied verbatim. The second type proves a layout that WAS read is
        // carried and is NOT labelled, so the label discriminates rather than always firing.
        var withLayout = description.Types.Single(t => t.ReferenceName == TypeThatHasALayout);
        withLayout.Layout.ShouldNotBeNull();
        withLayout.Unfetched.ShouldNotContain("formLayout");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 10 — inherited-vs-authored on types AND on rules (AB#238)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Inherited-vs-authored is marked on types and on rules alike.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Spec test 10 — ruling S3's other half.</b> Carrying everything is only useful if
    /// the reader can tell the classes apart: the ~55 inherited rules on a derived type must
    /// not drown the one or two someone actually wrote, and the tag is what stops them doing
    /// so. Asserted on BOTH levels because the type-level marking shipped in AB#234 and could
    /// pass this alone while rules carried no tag at all.
    /// </remarks>
    [Fact]
    public async Task Assemble_InheritedVsAuthored_IsMarkedOnTypesAndOnRules()
    {
        var description = (await BuildAssembler(BuildSource()).AssembleAsync(null, FixedCapture)).ShouldBeAssembled();

        // On TYPES: the fixture's roster mixes custom and inherited.
        description!.Types.Select(t => t.Customization).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(["custom", "inherited"]);
        description.Types.Single(t => t.ReferenceName == "Microsoft.VSTS.WorkItemTypes.Task")
            .Customization.ShouldBe("inherited");
        description.Types.Single(t => t.ReferenceName == TypeWithRules)
            .Customization.ShouldBe("custom");

        // 🔴 On RULES: both classes present and distinguishable, on the same type.
        var rules = description.Types.Single(t => t.ReferenceName == TypeWithRules).Rules;
        rules.Select(r => r.Customization.Kind).ShouldBe(
            [RuleCustomizationKind.Custom, RuleCustomizationKind.System],
            "the reader must be able to tell an authored rule from inherited plumbing — that "
            + "distinction is what makes carrying all of them bearable");

        // The tag carries the server's own word, not a paraphrase.
        rules.Select(r => r.Customization.Token).ShouldBe(["custom", "system"]);
    }
}
