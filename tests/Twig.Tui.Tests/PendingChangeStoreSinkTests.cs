using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Twig.Tui.Views;
using Xunit;

namespace Twig.Tui.Tests;

/// <summary>
/// 🔴 <b>The acceptance floor for milestone M3 (ADO #183, wayfinder 0005 §1/§7).</b>
/// </summary>
/// <remarks>
/// <para>
/// Two claims, and the second is the milestone. First, Twig ships an
/// <see cref="IChangeSink"/> over <see cref="IPendingChangeStore"/>. Second — and this is
/// what M3 is for — <c>WorkItemFormView.EditableFieldRefs</c> is a <i>consequence</i> of what
/// that sink declares rather than a hard-coded list that happens to agree with it. The three
/// fields are unchanged; the reason is not.
/// </para>
/// <para>
/// The arm that carries the milestone is
/// <see cref="EditableFieldRefs_TracksTheSinkDeclaration_NotAConstant"/> and its painting
/// sibling: they hand the view a sink declaring a <i>different</i> set and require the
/// editable rows to follow. Restore the hard-coded list and exactly those arms go red while
/// the count-of-three arms stay green — which is why a count assertion alone could never have
/// proven this.
/// </para>
/// </remarks>
public class PendingChangeStoreSinkTests
{
    private static WorkItem Item(int id = 42, string title = "Story") =>
        new WorkItemBuilder(id, title)
            .AsUserStory()
            .InState("Active")
            .AssignedTo("Alice")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .Build();

    private static WorkItemDetailDocument DocumentFor(WorkItem item, params string[] extraFields)
    {
        var snapshot = new Twig.Domain.Services.WorkItemMapper().ToSnapshot(item);
        if (extraFields.Length > 0)
        {
            var fields = new Dictionary<string, string?>(snapshot.Fields);
            foreach (var f in extraFields) fields[f] = "carried";
            snapshot = snapshot with { Fields = fields };
        }

        return WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot);
    }

    /// <summary>A sink that declares an arbitrary set — the second answer M3 must defer to.</summary>
    private sealed class DeclaringSink(params string[] fieldRefs) : IChangeSink
    {
        public IReadOnlySet<string> PersistableFieldRefs { get; } =
            new HashSet<string>(fieldRefs, StringComparer.OrdinalIgnoreCase);

        public Task<SubmitOutcome> SubmitAsync(ChangeProposal proposal, CancellationToken ct = default) =>
            Task.FromResult<SubmitOutcome>(new Saved(1));
    }

    private static IPendingChangeStore Store()
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return store;
    }

    // ── The milestone: derivation, not coincidence ──────────────────

    [Fact]
    public void EditableFieldRefs_TracksTheSinkDeclaration_NotAConstant()
    {
        // 🔴 THE M3 ARM. A sink declaring a wholly different set must move the editable set
        // with it. A hard-coded list agrees with the shipped sink by coincidence and fails
        // here, which is the only way to tell derivation from agreement.
        var form = new WorkItemFormView(
            Store(),
            new DeclaringSink("Microsoft.VSTS.Common.Priority", "System.Tags"));

        form.EditableFieldRefs.ShouldBe(
            new HashSet<string> { "Microsoft.VSTS.Common.Priority", "System.Tags" },
            ignoreOrder: true);

        form.EditableFieldRefs.ShouldNotContain("System.Title");
    }

    [Fact]
    public void PaintedRows_AreTypableAccordingToTheSink_NotAConstant()
    {
        // The declaration has to reach the painted widgets, not just a property. A sink that
        // cannot persist System.Title must leave that row untypable even though every
        // previous version of this view hard-coded it as editable.
        var item = Item();
        var form = new WorkItemFormView(
            Store(),
            new DeclaringSink("Microsoft.VSTS.Common.Priority"));

        form.LoadDocument(DocumentFor(item, "Microsoft.VSTS.Common.Priority"), item);

        form.EditorFor("Microsoft.VSTS.Common.Priority").ShouldNotBeNull(
            "the sink declared this field, so its row must accept typing");
        form.EditorFor("System.Title").ShouldBeNull(
            "the sink cannot persist System.Title, so typing into it would be silently eaten");
        form.EditorFor("System.State").ShouldBeNull();
        form.EditorFor("System.AssignedTo").ShouldBeNull();
    }

    [Fact]
    public void EditableFieldRefs_EmptySink_MakesTheWholeFormReadOnly()
    {
        // The degenerate end of the same rule: declare nothing, persist nothing, invite
        // nothing. A constant list cannot reach this state at all.
        var item = Item();
        var form = new WorkItemFormView(Store(), new DeclaringSink());

        form.LoadDocument(DocumentFor(item), item);

        form.EditableFieldRefs.ShouldBeEmpty();
        form.FieldOrder.ShouldNotBeEmpty();
        form.FieldOrder.ShouldAllBe(f => form.EditorFor(f) == null);
    }

    [Fact]
    public void EditableFieldRefs_IsTheSameObjectTheSinkDeclares()
    {
        // Reference identity, so a copy taken at construction — a parallel list by another
        // name, which would silently stop tracking — cannot pass.
        var sink = new DeclaringSink("System.Title");
        var form = new WorkItemFormView(Store(), sink);

        form.EditableFieldRefs.ShouldBeSameAs(sink.PersistableFieldRefs);
    }

    [Fact]
    public void DefaultConstruction_DerivesFromTheShippedSink_SameThreeFields()
    {
        // The value does not change in M3 — only where it comes from.
        var form = new WorkItemFormView(Store());

        form.EditableFieldRefs.ShouldBe(
            new HashSet<string> { "System.Title", "System.State", "System.AssignedTo" },
            ignoreOrder: true);
    }

    [Fact]
    public void DocumentReadOnlyFlag_IsStillNotConsulted_WhenTheSinkDeclaresTheField()
    {
        // DetailControl.ReadOnly stays reported-never-enforced (0002 §6). ADO marks almost
        // nothing read-only, so making it authoritative would open nearly the whole form.
        // The inverse — a sink-declared field the server flags read-only — stays typable.
        var item = Item();
        var snapshot = new Twig.Domain.Services.WorkItemMapper().ToSnapshot(item);
        var layout = new FormLayout("User Story", "process-guid",
        [
            new LayoutPage("p1", "Details", "custom", true, false,
            [
                new LayoutSection("s1",
                [
                    new LayoutGroup("g1", "Group", true, false,
                    [
                        new LayoutControl("System.Title", "Title", "FieldControl", true, true, false),
                    ]),
                ]),
            ]),
        ]);

        var form = new WorkItemFormView(Store(), new DeclaringSink("System.Title"));
        form.LoadDocument(WorkItemDetailProjector.Project(layout, snapshot), item);

        form.EditorFor("System.Title").ShouldNotBeNull();
    }

    [Fact]
    public void EditableFieldRefs_MatchesFieldRefsCaseInsensitively()
    {
        // Field reference names are the join key across the projection, the capability, and
        // the sink; ADO is not case-consistent about them.
        //
        // Run this through the SHIPPED sink rather than the test's own DeclaringSink: the stub
        // builds its own OrdinalIgnoreCase set, so asserting against it only proves that
        // StringComparer.OrdinalIgnoreCase is case-insensitive and stays green even if the
        // shipped sink's comparer regresses to the default ordinal one.
        new PendingChangeStoreSink(Store())
            .PersistableFieldRefs.Contains("system.title").ShouldBeTrue();
    }

    // ── The shipped sink itself ─────────────────────────────────────

    [Fact]
    public void ShippedSink_DeclaresTheThreeFieldsTheFlushPathCanCarry()
    {
        new PendingChangeStoreSink(Store()).PersistableFieldRefs.ShouldBe(
            new HashSet<string> { "System.Title", "System.State", "System.AssignedTo" },
            ignoreOrder: true);
    }

    [Fact]
    public async Task Submit_FieldEdit_StagesOneFieldRow()
    {
        var store = Store();
        var sink = new PendingChangeStoreSink(store).BoundTo(42, 7);

        var outcome = await sink.SubmitAsync(new FieldEdit("System.Title", "Old", "New"));

        outcome.ShouldBeUnionCase<Saved>().Revision.ShouldBe(7);
        await store.Received(1).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(rows =>
                rows.Count == 1 &&
                rows[0].Item1 == "field" &&
                rows[0].Item2 == "System.Title" &&
                rows[0].Item3 == "Old" &&
                rows[0].Item4 == "New"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_StateMove_StagesAStateRow_WithAccompanyingEditsInOneBatch()
    {
        // One unit of work, one transaction: a partial failure cannot leave the state moved
        // without the edits that were meant to ride with it.
        var store = Store();
        var sink = new PendingChangeStoreSink(store).BoundTo(42, 3);

        var outcome = await sink.SubmitAsync(new StateMove("Active", "Closed",
            [new FieldEdit("System.AssignedTo", "Alice", "Bob")]));

        outcome.ShouldBeUnionCase<Saved>().Revision.ShouldBe(3);
        await store.Received(1).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(rows =>
                rows.Count == 2 &&
                rows[0].Item1 == "state" &&
                rows[0].Item2 == "System.State" &&
                rows[0].Item3 == "Active" &&
                rows[0].Item4 == "Closed" &&
                rows[1].Item1 == "field" &&
                rows[1].Item2 == "System.AssignedTo" &&
                // The accompanying edit's VALUES, not just its ref: a batching path that
                // carried the field but dropped or swapped prior/proposed is exactly the
                // silent loss this arm exists to catch.
                rows[1].Item3 == "Alice" &&
                rows[1].Item4 == "Bob"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_UndeclaredField_IsRefused_AndNothingIsStaged()
    {
        // A sink that accepted a field it does not declare would be the silent-loss failure
        // wearing the contract's clothes.
        var store = Store();
        var sink = new PendingChangeStoreSink(store).BoundTo(42, 1);

        var outcome = await sink.SubmitAsync(
            new FieldEdit("Microsoft.VSTS.Common.Priority", "2", "1"));

        outcome.ShouldBeUnionCase<Refused>().Reason.ShouldContain("Microsoft.VSTS.Common.Priority");
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Submit_StateMoveWithUndeclaredAccompanyingEdit_IsRefused_WholeMoveDropped()
    {
        var store = Store();
        var sink = new PendingChangeStoreSink(store).BoundTo(42, 1);

        var outcome = await sink.SubmitAsync(new StateMove("Active", "Closed",
            [new FieldEdit("Microsoft.VSTS.Common.Priority", "2", "1")]));

        outcome.ShouldBeUnionCase<Refused>();
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Submit_UnboundSink_IsRefused_RatherThanStagingAgainstNothing()
    {
        var store = Store();

        var outcome = await new PendingChangeStoreSink(store)
            .SubmitAsync(new FieldEdit("System.Title", "Old", "New"));

        outcome.ShouldBeUnionCase<Refused>().Reason.ShouldContain("not bound");
        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Submit_StoreThrows_IsReportedAsRefused_NotAsAnEscapingException()
    {
        // SubmitOutcome is the contract's channel for "this did not land". A host should not
        // have to wrap every submit in a try/catch to avoid losing a change silently.
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var outcome = await new PendingChangeStoreSink(store).BoundTo(42, 1)
            .SubmitAsync(new FieldEdit("System.Title", "Old", "New"));

        outcome.ShouldBeUnionCase<Refused>().Reason.ShouldContain("DB error");
    }

    [Fact]
    public async Task Submit_DeclaredFieldInDifferentCase_IsStaged()
    {
        var store = Store();

        var outcome = await new PendingChangeStoreSink(store).BoundTo(42, 1)
            .SubmitAsync(new FieldEdit("system.title", "Old", "New"));

        outcome.ShouldBeUnionCase<Saved>().Revision.ShouldBe(1);

        // The name says "IsStaged", so assert the row actually reached the store. The open
        // question this arm exists to pin is what the staged ref looks like: the sink passes
        // the caller's spelling through rather than canonicalising it, so the flush path must
        // match case-insensitively too. Asserting only the Saved outcome would let a sink that
        // accepted the variant and staged nothing pass.
        await store.Received(1).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(rows =>
                rows.Count == 1 &&
                rows[0].Item1 == "field" &&
                rows[0].Item2 == "system.title" &&
                rows[0].Item3 == "Old" &&
                rows[0].Item4 == "New"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BoundTo_DoesNotMutateTheDeclaration()
    {
        // Binding is about where changes land, not what may be edited — the declaration has
        // to be answerable before any item is loaded, which is what lets the view derive
        // EditableFieldRefs at construction time.
        var sink = new PendingChangeStoreSink(Store());

        sink.BoundTo(42, 1).PersistableFieldRefs.ShouldBe(sink.PersistableFieldRefs, ignoreOrder: true);
    }

    // ── The capability composes over it unchanged ───────────────────

    [Fact]
    public void EditCapability_OverTheShippedSink_ReportsTheSameEditableSet()
    {
        // The M2 types were not modified for M3; this asserts the sink drops into them as-is.
        var capability = new EditCapability(
            new PendingChangeStoreSink(Store()), WorkItemType.UserStory);

        capability.EditableFieldRefs.ShouldBe(
            new HashSet<string> { "System.Title", "System.State", "System.AssignedTo" },
            ignoreOrder: true);
        capability.CanEdit("System.Title").ShouldBeTrue();
        capability.CanEdit("Microsoft.VSTS.Common.Priority").ShouldBeFalse();
    }
}
