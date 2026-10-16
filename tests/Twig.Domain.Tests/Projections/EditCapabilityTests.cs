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
public class EditCapabilityTests
{
    private static readonly string[] ThreeFields =
        ["System.Title", "System.State", "System.AssignedTo"];

    private sealed class StubSink(params string[] fields) : IChangeSink
    {
        public IReadOnlySet<string> PersistableFieldRefs { get; } =
            new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

        public Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default)
        {
            return Task.FromResult<SubmitOutcome>(new Saved(2));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §1 — the SINK declares mutability, not DetailControl.ReadOnly
    // ═══════════════════════════════════════════════════════════════


    /// <summary>
    /// 🔴 The two-sink obligation (0005 §7 / 0006 §6) in miniature: a DIFFERENT sink must
    /// produce a DIFFERENT editable set. Two sinks declaring the same fields would prove the
    /// interface compiles, not that the seam carries the decision.
    /// </summary>
    [Fact]
    public void DifferentSink_ProducesDifferentEditableSet()
    {
        var sinkA = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);
        var sinkB = new EditCapability(new StubSink("System.Description", "Custom.Severity"), WorkItemType.Task, isSeed: false);

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
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);

        capability.CanEdit("system.title").ShouldBeTrue();

        // The negative side belongs in THIS arm: asserting only the true case passes against a
        // CanEdit hard-wired to true, which would make the case-insensitivity claim vacuous.
        capability.CanEdit("custom.priority").ShouldBeFalse();
    }

    [Fact]
    public void Validate_FieldEditOnUndeclaredField_IsRejected()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);

        var outcome = capability.Validate(new FieldEdit("Custom.Priority", "1", "2"));

        outcome.ShouldBeUnionCase<Rejected>().Reason.ShouldContain("Custom.Priority");
    }

    [Fact]
    public void Validate_FieldEditOnDeclaredField_IsAccepted()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);

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
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false, TaskProcess());

        // Assert the EXACT offer set, not merely that the current state is absent: an
        // OfferedStates gutted to `return []` satisfies a bare ShouldNotContain, which would
        // make this arm — and the precondition at Validate_IllegalTransition below — hollow.
        capability.OfferedStates("Active").ShouldBe(["New", "Closed"], ignoreOrder: true);
        capability.OfferedStates("Active").ShouldNotContain("Active");
    }

    [Fact]
    public void OfferedStates_WithoutProcessConfiguration_IsEmpty_NotEverything()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);

        // Degrades to "I don't know", never to a confident wrong answer.
        capability.OfferedStates("Active").ShouldBeEmpty();
    }

    [Fact]
    public void UninitializedSeedOffersConfiguredStatesWithoutBypassingFieldOrStateValidation()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: true, TaskProcess());

        capability.OfferedStates(string.Empty).ShouldBe(["New", "Active", "Closed"]);
        capability.Validate(new StateMove(string.Empty, "new", []))
            .ShouldBeUnionCase<Accepted>();
        capability.Validate(new StateMove(string.Empty, "Unknown", []))
            .ShouldBeUnionCase<Rejected>();
        capability.Validate(new StateMove("New", "Unknown", []))
            .ShouldBeUnionCase<Rejected>();
        capability.Validate(new StateMove(string.Empty, "New", [new FieldEdit("Custom.Unsupported", null, "Value")]))
            .ShouldBeUnionCase<Rejected>();
    }

    [Fact]
    public void EmptyPublishedStateCannotUseSeedInitialization()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false, TaskProcess());

        capability.OfferedStates(string.Empty).ShouldBeEmpty();
        capability.Validate(new StateMove(string.Empty, "New", []))
            .ShouldBeUnionCase<Rejected>();
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
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false, process);

        // Precondition: this transition really is absent from the offer list, so the arm is
        // testing refusal rather than coincidence. Asserted against the exact populated set —
        // a ShouldNotContain alone is also satisfied by an empty list, which would establish
        // the opposite of the precondition it claims to prove.
        var offered = capability.OfferedStates("Closed");
        offered.ShouldBe(["New", "Active"], ignoreOrder: true);
        offered.ShouldNotContain("Nonexistent");

        var outcome = capability.Validate(new StateMove("Closed", "Nonexistent", []));

        outcome.ShouldBeUnionCase<Rejected>();
    }

    [Fact]
    public void Validate_StateMove_WithoutProcessConfiguration_IsAccepted_ServerRemainsFinal()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false);

        // No rules to judge by means no basis to refuse. Twig does not invent a refusal it
        // cannot justify — the server is the authority either way.
        capability.Validate(new StateMove("Active", "Closed", []))
            .ShouldBeUnionCase<Accepted>();
    }

    [Fact]
    public void Validate_StateMove_WithUndeclaredAccompanyingField_IsRejected()
    {
        var capability = new EditCapability(new StubSink(ThreeFields), WorkItemType.Task, isSeed: false, TaskProcess());

        var outcome = capability.Validate(new StateMove(
            "New", "Active", [new FieldEdit("Custom.Resolution", null, "Fixed")]));

        outcome.ShouldBeUnionCase<Rejected>().Reason.ShouldContain("Custom.Resolution");
    }

}
