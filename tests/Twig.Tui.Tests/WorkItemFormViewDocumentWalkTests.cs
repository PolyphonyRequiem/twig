using System.Reflection;
using NSubstitute;
using Shouldly;
using Terminal.Gui.Views;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Twig.Tui.Views;
using Xunit;

namespace Twig.Tui.Tests;

/// <summary>
/// 🔴 <b>The acceptance floor for wayfinder-detail-projection ticket 0004.</b>
/// </summary>
/// <remarks>
/// <para>
/// The trap this ticket exists to avoid is TWO field-selection implementations drifting
/// apart. These tests fail if <see cref="WorkItemFormView"/> silently returns to one: the
/// structural arms assert it declares no pre-built field widgets, and the behavioural arms
/// assert every painted row came from the document — including rows the old hard-coded list
/// never had, and excluding ones it always had.
/// </para>
/// <para>
/// Each of these fails against the pre-0004 implementation, which is the bar
/// <c>AGENTS.md</c>'s testing conventions set. The old view had no
/// <c>LoadDocument</c> at all, ten <c>TextField</c> members, and a fixed row set.
/// </para>
/// </remarks>
public class WorkItemFormViewDocumentWalkTests
{
    private static WorkItem Item(int id = 42, string title = "Story") =>
        new WorkItemBuilder(id, title)
            .AsUserStory()
            .InState("Active")
            .AssignedTo("Alice")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .Build();

    private static WorkItemDetailDocument Document(params LayoutControl[] controls) =>
        WorkItemDetailProjector.Project(
            new FormLayout("User Story", "process-guid",
            [
                new LayoutPage("p1", "Details", "custom", true, false,
                [
                    new LayoutSection("s1", [new LayoutGroup("g1", "Group", true, false, controls)]),
                ]),
            ]),
            Snapshot());

    private static WorkItemSnapshot Snapshot(Dictionary<string, string?>? fields = null) => new()
    {
        Id = 42,
        Revision = 3,
        TypeName = "User Story",
        Title = "Story",
        State = "Active",
        AssignedTo = "Alice",
        IterationPath = "Project\\Sprint 1",
        AreaPath = "Project\\Team",
        Fields = fields ?? new Dictionary<string, string?>(),
    };

    private static LayoutControl Field(string refName, string label, bool readOnly = false) =>
        new(refName, label, "FieldControl", readOnly, true, false);

    private static WorkItemFormView NewForm() =>
        new(Substitute.For<IPendingChangeStore>());

    // ── Structural: the hard-coded list cannot come back ────────────

    [Fact]
    public void View_DeclaresNoPreBuiltFieldWidgets()
    {
        // The pre-0004 view declared ten: _titleField, _stateField, _assignedToField,
        // _iterationField, _areaField, _effortField, _priorityField, _tagsField,
        // _descriptionField (TextField) plus _idLabel/_typeLabel (Label). Every one of them
        // encoded a "this form shows THIS field" decision that the document now owns.
        var fieldWidgets = typeof(WorkItemFormView)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(TextField))
            .Select(f => f.Name)
            .ToList();

        fieldWidgets.ShouldBeEmpty(
            $"WorkItemFormView must not declare per-field widgets; found: {string.Join(", ", fieldWidgets)}");
    }

    [Fact]
    public void View_ExposesNoPerFieldLoadEntrypoint()
    {
        // LoadWorkItem(WorkItem) was the old entrypoint: given an aggregate, the view decided
        // for itself which fields to show. The only load path now requires a document.
        var loadMethods = typeof(WorkItemFormView)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Load", StringComparison.Ordinal))
            .ToList();

        loadMethods.ShouldNotBeEmpty();
        foreach (var method in loadMethods)
        {
            method.GetParameters()
                .Select(p => p.ParameterType)
                .ShouldContain(typeof(WorkItemDetailDocument),
                    $"{method.Name} must take the shared document; a load path that does not " +
                    "is a second field-selection implementation.");
        }
    }

    [Fact]
    public void View_HasNoFieldReferenceNameConstantsBeyondTheEditableSet()
    {
        // The editable set is an EDITABILITY decision (which rows accept typing), bounded by
        // what IPendingChangeStore persists — not a decision about which rows exist. Anything
        // beyond it that looks like a field reference name is field selection creeping back.
        var refNameLiterals = typeof(WorkItemFormView)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .Where(v => v is not null && (v.StartsWith("System.", StringComparison.Ordinal)
                || v.StartsWith("Microsoft.VSTS.", StringComparison.Ordinal)))
            .ToList();

        refNameLiterals.ShouldBeEmpty();
        NewForm().EditableFieldRefs.Count.ShouldBe(3);
    }

    // ── Behavioural: the rows ARE the document's ────────────────────

    [Fact]
    public void LoadDocument_PaintsExactlyTheDocumentsControls_InOrder()
    {
        var form = NewForm();
        var document = Document(
            Field("System.Title", "Title"),
            Field("Microsoft.VSTS.Common.Priority", "Priority", readOnly: true),
            Field("System.State", "State"));

        form.LoadDocument(document, Item());

        form.FieldOrder.ShouldBe(
            ["System.Title", "Microsoft.VSTS.Common.Priority", "System.State"]);
    }

    [Fact]
    public void LoadDocument_PaintsAProcessSpecificFieldTheOldListNeverKnew()
    {
        // A custom-process field. The pre-0004 view could not show it at any value, because
        // it had no widget for it. This is the migration's whole payoff.
        var form = NewForm();

        form.LoadDocument(Document(Field("Contoso.Compliance.ReviewTicket", "Review ticket")), Item());

        form.FieldOrder.ShouldContain("Contoso.Compliance.ReviewTicket");
    }

    [Fact]
    public void LoadDocument_OmitsAFieldTheDocumentDoesNotCarry()
    {
        // The other direction, and the sharper half: a form whose server layout has no
        // Description must not sprout one. A surviving hard-coded row would show up here.
        var form = NewForm();

        form.LoadDocument(Document(Field("System.Title", "Title")), Item());

        form.FieldOrder.ShouldBe(["System.Title"]);
        form.DisplayedValue("System.Description").ShouldBeNull();
        form.DisplayedValue("Microsoft.VSTS.Common.Priority").ShouldBeNull();
        form.DisplayedValue("System.Tags").ShouldBeNull();
    }

    [Fact]
    public void LoadDocument_EmptyDocument_PaintsAnEmptyForm()
    {
        // 🔴 The single most load-bearing assertion here. If ANY field row survives a
        // document with no controls, a hard-coded list is still reachable. The pre-0004 view
        // painted ten rows no matter what it was given.
        var form = NewForm();

        form.LoadDocument(
            WorkItemDetailProjector.Project(new FormLayout("User Story", "p", []), Snapshot()),
            Item());

        form.FieldOrder.ShouldBeEmpty();
    }

    [Fact]
    public void LoadDocument_UsesTheServersLabels_NotTwigsOwn()
    {
        var form = NewForm();

        form.LoadDocument(Document(Field("System.Title", "Ticket headline")), Item());

        form.LabelOrder.ShouldBe(["Ticket headline"]);
    }

    [Fact]
    public void LoadDocument_PreservesServerOrder_EvenWhenItContradictsTheOldFixedOrder()
    {
        // The old view's order was hard-coded: id, type, title, state, assignedTo, iteration,
        // area, effort, priority, tags, description. Reversing it proves the order is read,
        // not remembered.
        var form = NewForm();

        form.LoadDocument(Document(
            Field("System.AreaPath", "Area"),
            Field("System.State", "State"),
            Field("System.Title", "Title")), Item());

        form.FieldOrder.ShouldBe(["System.AreaPath", "System.State", "System.Title"]);
    }

    [Fact]
    public void LoadDocument_SwitchingItems_ReplacesRowsRatherThanAccumulating()
    {
        var form = NewForm();

        form.LoadDocument(Document(Field("System.Title", "Title")), Item(1));
        form.LoadDocument(Document(Field("System.State", "State")), Item(2));

        form.FieldOrder.ShouldBe(["System.State"]);
    }

    // ── The three states reach the pane ─────────────────────────────

    [Fact]
    public void LoadDocument_DistinguishesEmptyOnServerFromNotCarriedByTwig()
    {
        // The defect 0002 §3 exists to make visible, and the exact one the pre-0004 view
        // inherited by reading item.Fields[key] directly: both cases rendered as blank.
        var snapshot = Snapshot(new Dictionary<string, string?> { ["System.Tags"] = "" });
        var document = WorkItemDetailProjector.Project(
            new FormLayout("User Story", "process-guid",
            [
                new LayoutPage("p1", "Details", "custom", true, false,
                [
                    new LayoutSection("s1",
                    [
                        new LayoutGroup("g1", "Group", true, false,
                        [
                            Field("System.Tags", "Tags"),
                            Field("Contoso.Compliance.SignedOff", "Signed off"),
                        ]),
                    ]),
                ]),
            ]),
            snapshot);

        var form = NewForm();
        form.LoadDocument(document, Item());

        form.StateOf("System.Tags").ShouldBe(DetailFieldState.EmptyOnServer);
        form.StateOf("Contoso.Compliance.SignedOff").ShouldBe(DetailFieldState.NotCarriedByTwig);
        form.DisplayedValue("System.Tags").ShouldBe("");
        form.DisplayedValue("Contoso.Compliance.SignedOff").ShouldBe("<not carried by twig>");
    }

    [Fact]
    public void LoadDocument_ResolvesCoreFieldsThroughTheProjection_NotAFieldsLookup()
    {
        // System.Title is absent from WorkItemSnapshot.Fields entirely. A direct dictionary
        // read blanks it; the projection reads the snapshot property.
        var form = NewForm();

        form.LoadDocument(Document(Field("System.Title", "Title")), Item());

        form.StateOf("System.Title").ShouldBe(DetailFieldState.HasValue);
        form.DisplayedValue("System.Title").ShouldBe("Story");
    }

    // ── Editing stays separate from painting ────────────────────────

    [Fact]
    public void LoadDocument_OnlyTheEditableSetIsTypable()
    {
        var form = NewForm();

        form.LoadDocument(Document(
            Field("System.Title", "Title"),
            Field("Microsoft.VSTS.Common.Priority", "Priority")), Item());

        form.EditorFor("System.Title").ShouldNotBeNull();
        form.EditorFor("Microsoft.VSTS.Common.Priority").ShouldBeNull();
    }

    [Fact]
    public void LoadDocument_ServerReadOnlyIsNotEnforced_OnlyReported()
    {
        // 0002 §6: ReadOnly is reported, never enforced. A server-read-only Title still
        // accepts typing here, because editability is the TUI's own bounded decision.
        var form = NewForm();

        form.LoadDocument(Document(Field("System.Title", "Title", readOnly: true)), Item());

        form.EditorFor("System.Title").ShouldNotBeNull();
    }

    [Fact]
    public void ReadOnlyDocumentPainting_NeverTouchesThePendingChangeStore()
    {
        // Read-only construction must never require a store (0002 §10). Painting is
        // read-only; only OnSave writes.
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);

        form.LoadDocument(Document(Field("System.Title", "Title")), Item());

        store.ReceivedCalls().ShouldBeEmpty();
    }

    // ── The fallback reaches the pane as an ordinary document ───────

    [Fact]
    public void LoadDocument_FallbackLayout_PaintsCoreFieldsAndIsIndistinguishableToTheView()
    {
        // The absent-layout answer: the view is handed a document like any other and cannot
        // tell it came from the fallback. That is what stops the degraded path becoming a
        // second field-selection implementation.
        var snapshot = Snapshot(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
        });

        var form = NewForm();
        form.LoadDocument(
            WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot),
            Item());

        form.FieldOrder.ShouldContain("System.Title");
        form.FieldOrder.ShouldContain("Microsoft.VSTS.Common.Priority");
        form.StateOf("System.Title").ShouldBe(DetailFieldState.HasValue);
        form.EditorFor("System.Title").ShouldNotBeNull();
    }
}
