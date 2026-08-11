using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Projections;

/// <summary>
/// Acceptance floor for M2's editing capability (wayfinder ticket 0005, ticket 0006 §5).
/// </summary>
/// <remarks>
/// These arms are structural AND behavioural. The behavioural ones were red-green verified by
/// reintroducing each defect on the fixed code — see the comment above each such arm naming
/// what was broken and which arms failed.
/// </remarks>
public class EditCapabilityTests
{
    private static readonly string[] ThreeFields =
        ["System.Title", "System.State", "System.AssignedTo"];

    private sealed class StubSink(params string[] fields) : IChangeSink
    {
        public IReadOnlySet<string> PersistableFieldRefs { get; } =
            new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

        public ChangeProposal? LastSubmitted { get; private set; }

        public Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default)
        {
            LastSubmitted = proposal;
            return Task.FromResult<SubmitOutcome>(new Saved(2));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §1 — the SINK declares mutability, not DetailControl.ReadOnly
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EditableFieldRefs_IsTheSinksDeclaration_NotAConstant()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        capability.EditableFieldRefs.ShouldBe(ThreeFields, ignoreOrder: true);
    }

    /// <summary>
    /// 🔴 The two-sink obligation (0005 §7 / 0006 §6) in miniature: a DIFFERENT sink must
    /// produce a DIFFERENT editable set. Two sinks declaring the same fields would prove the
    /// interface compiles, not that the seam carries the decision.
    /// </summary>
    [Fact]
    public void DifferentSink_ProducesDifferentEditableSet()
    {
        var sinkA = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);
        var sinkB = new EditCapability(new StubSink("System.Description", "Custom.Severity"), WorkItemType.Task);

        // The precondition, asserted rather than assumed — if the two sets ever coincided this
        // arm would pass while proving nothing.
        sinkA.EditableFieldRefs.SetEquals(sinkB.EditableFieldRefs).ShouldBeFalse();

        sinkA.CanEdit("System.Title").ShouldBeTrue();
        sinkB.CanEdit("System.Title").ShouldBeFalse();
        sinkB.CanEdit("Custom.Severity").ShouldBeTrue();
    }

    [Fact]
    public void CanEdit_IsCaseInsensitive_MatchingAdoFieldRefSemantics()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        capability.CanEdit("system.title").ShouldBeTrue();
    }

    [Fact]
    public void Validate_FieldEditOnUndeclaredField_IsRejected()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        var outcome = capability.Validate(new FieldEdit("Custom.Priority", "1", "2"));

        outcome.ShouldBeUnionCase<Rejected>().Reason.ShouldContain("Custom.Priority");
    }

    [Fact]
    public void Validate_FieldEditOnDeclaredField_IsAccepted()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        capability.Validate(new FieldEdit("System.Title", "old", "new"))
            .ShouldBeUnionCase<Accepted>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  §3 — transitions: offer-time filter AND entry-time validation
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration TaskProcess() =>
        new ProcessConfigBuilder()
            .AddType("Task", ["New", "Active", "Closed"])
            .Build();

    [Fact]
    public void OfferedStates_ExcludesTheCurrentState()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, TaskProcess());

        capability.OfferedStates("Active").ShouldNotContain("Active");
    }

    [Fact]
    public void OfferedStates_WithoutProcessConfiguration_IsEmpty_NotEverything()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        // Degrades to "I don't know", never to a confident wrong answer.
        capability.OfferedStates("Active").ShouldBeEmpty();
    }

    /// <summary>
    /// 🔴 The entry-time half of §3. A host that ignored <c>OfferedStates</c> entirely must
    /// still be refused. This is the arm that fails if someone deletes the re-validation on the
    /// grounds that the offer list already filtered — red-green verified: removing the
    /// <c>StateTransitionService.Evaluate</c> call in <c>Validate</c> fails this arm alone.
    /// </summary>
    [Fact]
    public void Validate_IllegalTransition_IsRefusedEvenIfHostIgnoredTheOfferList()
    {
        var process = TaskProcess();
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, process);

        // Precondition: this transition really is absent from the offer list, so the arm is
        // testing refusal rather than coincidence.
        capability.OfferedStates("Closed").ShouldNotContain("Nonexistent");

        var outcome = capability.Validate(new StateMove("Closed", "Nonexistent", []));

        outcome.ShouldBeUnionCase<Rejected>().Reason.ShouldContain("advisory");
    }

    [Fact]
    public void Validate_StateMove_WithoutProcessConfiguration_IsAccepted_ServerRemainsFinal()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        // No rules to judge by means no basis to refuse. Twig does not invent a refusal it
        // cannot justify — the server is the authority either way.
        capability.Validate(new StateMove("Active", "Closed", []))
            .ShouldBeUnionCase<Accepted>();
    }

    [Fact]
    public void Validate_StateMove_WithUndeclaredAccompanyingField_IsRejected()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, TaskProcess());

        var outcome = capability.Validate(new StateMove(
            "New", "Active", [new FieldEdit("Custom.Resolution", null, "Fixed")]));

        outcome.ShouldBeUnionCase<Rejected>().Reason.ShouldContain("Custom.Resolution");
    }

    // ═══════════════════════════════════════════════════════════════
    //  §4 — a state move is its own kind, and carries field edits
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void StateMove_IsADistinctProposalKind_NotAFieldEditNamedSystemState()
    {
        ChangeProposal move = new StateMove("New", "Active", []);
        ChangeProposal edit = new FieldEdit("System.State", "New", "Active");

        move.ShouldBeUnionCase<StateMove>();
        edit.ShouldBeUnionCase<FieldEdit>();
    }

    [Fact]
    public void StateMove_CarriesAccompanyingFieldEdits_AsOneUnitOfWork()
    {
        var move = new StateMove("Active", "Closed",
            [new FieldEdit("System.AssignedTo", "alice", "bob")]);

        move.Accompanying.Count.ShouldBe(1);
        move.Accompanying[0].FieldRef.ShouldBe("System.AssignedTo");
    }

    // ═══════════════════════════════════════════════════════════════
    //  §5 — every change carries its prior value
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FieldEdit_CarriesPriorValue_ForDiffingAndUndo()
    {
        var edit = new FieldEdit("System.Title", "Before", "After");

        edit.PriorValue.ShouldBe("Before");
        edit.ProposedValue.ShouldBe("After");
    }

    [Fact]
    public void FieldEdit_NullPriorValue_MeansEmpty_NotUnknown()
    {
        var edit = new FieldEdit("System.Description", null, "Something");

        edit.PriorValue.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  §6 / 0006 §5 — the conflict carrier is narrow and revision-keyed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EditConflict_CarriesTheRemoteRevision_TheActualConcurrencyCheck()
    {
        var conflict = new EditConflict(7,
            [new ConflictedField("System.Title", "Base", "Mine", "Theirs")]);

        conflict.RemoteRevision.ShouldBe(7);
    }

    /// <summary>
    /// 🔴 The fourth fact. Twig's existing <c>FieldConflict</c> carries field/local/remote;
    /// this carrier adds the PROPOSED value, which is the one thing a resolver needs and Twig
    /// did not previously transport. An arm exists for it so a "simplification" back to the
    /// three-value shape fails loudly.
    /// </summary>
    [Fact]
    public void ConflictedField_CarriesAllFourValues_IncludingTheProposedOne()
    {
        var field = new ConflictedField("System.Title", "Base", "Mine", "Theirs");

        field.PriorValue.ShouldBe("Base");
        field.ProposedValue.ShouldBe("Mine");
        field.RemoteValue.ShouldBe("Theirs");
    }

    [Fact]
    public void EditConflict_CarriesOnlyCollidedFields_NotTheWholeForm()
    {
        var conflict = new EditConflict(7,
            [new ConflictedField("System.Title", "Base", "Mine", "Theirs")]);

        // A form has hundreds of controls; a collision has one to three. If this ever grew a
        // layout or a document, the error path would require a round trip the success path
        // does not — see EditConflict's remarks.
        conflict.Fields.Count.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  The sink round-trip
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubmitAsync_ReceivesTheProposal_AndReportsAnOutcome()
    {
        var sink = new StubSink(ThreeFields);
        ChangeProposal proposal = new FieldEdit("System.Title", "old", "new");

        var outcome = await sink.SubmitAsync(proposal);

        sink.LastSubmitted.ShouldNotBeNull();
        outcome.ShouldBeUnionCase<Saved>().Revision.ShouldBe(2);
    }

    [Fact]
    public void SubmitOutcome_HasThreeCases_SavedConflictedRefused()
    {
        SubmitOutcome saved = new Saved(3);
        SubmitOutcome conflicted = new Conflicted(new EditConflict(4, []));
        SubmitOutcome refused = new Refused("nope");

        saved.ShouldBeUnionCase<Saved>();
        conflicted.ShouldBeUnionCase<Conflicted>();
        refused.ShouldBeUnionCase<Refused>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  0002 §6 survives — ReadOnly is still reported, never enforced
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A control the SERVER marks read-only is still editable when the sink declares its field.
    /// This looks wrong and is deliberate: ADO marks almost nothing read-only, so treating the
    /// flag as authority would make nearly the whole form typable while the sink can persist a
    /// handful of fields — and the surplus edits would be silently eaten at save.
    /// </summary>
    [Fact]
    public void ServerReadOnlyFlag_DoesNotOverrideTheSinksDeclaration()
    {
        var control = new DetailControl(
            "System.Title", "Title", "FieldControl",
            ReadOnly: true, Visible: true, IsContribution: false,
            Value: new DetailFieldValue(DetailFieldState.HasValue, "A title", null));

        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task);

        control.ReadOnly.ShouldBeTrue();
        capability.CanEdit(control.Id).ShouldBeTrue();
    }
}
