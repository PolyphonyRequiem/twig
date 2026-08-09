using Shouldly;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Projections;

/// <summary>
/// Pins the detail document contract settled by wayfinder-detail-projection ticket 0002.
/// </summary>
public class WorkItemDetailProjectorTests
{
    private static FormLayout LayoutWith(params LayoutControl[] controls) =>
        new("Microsoft.VSTS.WorkItemTypes.UserStory", "process-1",
        [
            new LayoutPage("Details", "Details", "custom", true, false,
            [
                new LayoutSection("Section1",
                [
                    new LayoutGroup("g1", "Group", true, false, controls),
                ]),
            ]),
        ]);

    private static LayoutControl Field(string refName) =>
        new(refName, refName, "FieldControl", false, true, false);

    private static WorkItemSnapshot SnapshotWith(params (string Key, string? Value)[] fields) => new()
    {
        Id = 7,
        Revision = 3,
        TypeName = "User Story",
        Title = "A title the Fields dictionary does not contain",
        State = "Active",
        AssignedTo = "Someone",
        IterationPath = @"Twig\Sprint 1",
        AreaPath = @"Twig\Area",
        Fields = fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase),
    };

    private static DetailControl Only(WorkItemDetailDocument document) =>
        document.Pages.SelectMany(p => p.AllGroups).SelectMany(g => g.Controls).Single();

    [Fact]
    public void CoreFieldResolvesFromSnapshotProperty_NotFromTheFieldsDictionary()
    {
        // The whole point of the three-state model: System.Title is excluded from Fields
        // by FieldImportFilter, so a naive lookup blanks it.
        var snapshot = SnapshotWith();
        snapshot.Fields.ShouldNotContainKey("System.Title");

        var value = Only(WorkItemDetailProjector.Project(LayoutWith(Field("System.Title")), snapshot)).Value!;

        value.State.ShouldBe(DetailFieldState.HasValue);
        value.Full.ShouldBe("A title the Fields dictionary does not contain");
    }

    [Theory]
    [InlineData("System.Id", "7")]
    [InlineData("System.Rev", "3")]
    [InlineData("System.WorkItemType", "User Story")]
    [InlineData("System.State", "Active")]
    [InlineData("System.AssignedTo", "Someone")]
    [InlineData("System.AreaPath", @"Twig\Area")]
    public void AllEightCoreFieldsResolve(string refName, string expected)
    {
        var value = Only(WorkItemDetailProjector.Project(LayoutWith(Field(refName)), SnapshotWith())).Value!;

        value.State.ShouldBe(DetailFieldState.HasValue);
        value.Full.ShouldBe(expected);
    }

    [Fact]
    public void CarriedFieldWithAValueIsHasValue()
    {
        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Thing")), SnapshotWith(("Custom.Thing", "hello")))).Value!;

        value.State.ShouldBe(DetailFieldState.HasValue);
        value.Full.ShouldBe("hello");
        value.Short.ShouldBeNull("a short value needs no short form");
    }

    [Fact]
    public void CarriedFieldWithAnEmptyValueIsEmptyOnServer()
    {
        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Thing")), SnapshotWith(("Custom.Thing", "")))).Value!;

        value.State.ShouldBe(DetailFieldState.EmptyOnServer);
    }

    [Fact]
    public void AbsentFieldThatTwigWouldHaveImportedIsEmptyOnServer()
    {
        var definitions = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Thing"] = new("Custom.Thing", "Thing", "string", IsReadOnly: false),
        };

        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Thing")), SnapshotWith(), definitions)).Value!;

        value.State.ShouldBe(DetailFieldState.EmptyOnServer);
    }

    [Fact]
    public void AbsentBooleanFieldIsNotCarriedByTwig()
    {
        // FieldImportFilter refuses booleans outright — the string-only Fields dictionary
        // cannot represent JSON true/false faithfully.
        var definitions = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Flag"] = new("Custom.Flag", "Flag", "boolean", IsReadOnly: false),
        };

        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Flag")), SnapshotWith(), definitions)).Value!;

        value.State.ShouldBe(DetailFieldState.NotCarriedByTwig);
    }

    [Fact]
    public void AbsentReadOnlyFieldOffTheAllowlistIsNotCarriedByTwig()
    {
        var definitions = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.VSTS.Common.StackRank"] =
                new("Microsoft.VSTS.Common.StackRank", "Stack Rank", "double", IsReadOnly: true),
        };

        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Microsoft.VSTS.Common.StackRank")), SnapshotWith(), definitions)).Value!;

        value.State.ShouldBe(DetailFieldState.NotCarriedByTwig);
    }

    [Fact]
    public void AbsentFieldWithNoMetadataIsNotCarriedByTwig()
    {
        // Twig cannot honestly claim the server said blank when it does not know the field.
        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Unknown")), SnapshotWith())).Value!;

        value.State.ShouldBe(DetailFieldState.NotCarriedByTwig);
    }

    [Fact]
    public void LongValueCarriesTheFullSourceValueAndAShortForm()
    {
        var full = new string('x', 500);

        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("System.Description")), SnapshotWith(("System.Description", full)))).Value!;

        value.Full.ShouldBe(full, "the projection must never truncate the source value");
        value.Short.ShouldNotBeNull();
        value.Short!.Length.ShouldBeLessThan(full.Length);
        value.IsAbbreviated.ShouldBeTrue();
    }

    [Fact]
    public void MultilineValueGetsAShortFormEvenWhenItsFirstLineIsShort()
    {
        var value = Only(WorkItemDetailProjector.Project(
            LayoutWith(Field("Custom.Notes")), SnapshotWith(("Custom.Notes", "first\nsecond")))).Value!;

        value.Full.ShouldBe("first\nsecond");
        value.Short.ShouldBe("first…");
    }

    [Fact]
    public void ContributionControlCarriesNoValue()
    {
        var control = Only(WorkItemDetailProjector.Project(
            LayoutWith(new LayoutControl("ms.addin", "Add-in", "Contribution", false, true, IsContribution: true)),
            SnapshotWith()));

        control.IsContribution.ShouldBeTrue();
        control.Value.ShouldBeNull();
        control.ControlType.ShouldBe("Contribution");
    }

    [Fact]
    public void NonCustomPagesAreCarriedFlagged_NotFiltered()
    {
        var layout = new FormLayout("Type", "process-1",
        [
            new LayoutPage("Details", "Details", "custom", true, false, []),
            new LayoutPage("History", "History", "history", true, false, []),
            new LayoutPage("Links", "Links", "links", true, false, []),
            new LayoutPage("Attachments", "Attachments", "attachments", true, false, []),
        ]);

        var document = WorkItemDetailProjector.Project(layout, SnapshotWith());

        document.Pages.Count.ShouldBe(4);
        document.Pages.Select(p => p.PageType)
            .ShouldBe(["custom", "history", "links", "attachments"]);
        document.Pages.Count(p => p.CarriesFieldControls).ShouldBe(1);
    }

    [Fact]
    public void InvisibleAndReadOnlyControlsAreReportedNotFiltered()
    {
        var control = Only(WorkItemDetailProjector.Project(
            LayoutWith(new LayoutControl("Custom.Thing", "Thing", "FieldControl",
                ReadOnly: true, Visible: false, IsContribution: false)),
            SnapshotWith(("Custom.Thing", "v"))));

        control.ReadOnly.ShouldBeTrue();
        control.Visible.ShouldBeFalse();
        control.Value!.State.ShouldBe(DetailFieldState.HasValue);
    }

    [Fact]
    public void StructureAndOrderSurviveVerbatim()
    {
        var layout = new FormLayout("Microsoft.VSTS.WorkItemTypes.UserStory", "process-9",
        [
            new LayoutPage("p1", "Details", "custom", true, false,
            [
                new LayoutSection("Section1", [new LayoutGroup("gA", "A", true, false, [Field("F1")])]),
                new LayoutSection("Section2", [new LayoutGroup("gB", "B", true, false, [Field("F2")])]),
            ]),
        ]);

        var document = WorkItemDetailProjector.Project(layout, SnapshotWith());

        document.WorkItemId.ShouldBe(7);
        document.Revision.ShouldBe(3);
        document.WorkItemTypeReferenceName.ShouldBe("Microsoft.VSTS.WorkItemTypes.UserStory");
        document.ProcessId.ShouldBe("process-9");
        document.Pages[0].Sections.Select(s => s.Id).ShouldBe(["Section1", "Section2"]);
        document.Pages[0].AllGroups.Select(g => g.Id).ShouldBe(["gA", "gB"]);
    }

    [Fact]
    public void ControlTypeIsCarriedVerbatimForKindsTwigHasNeverHeardOf()
    {
        var control = Only(WorkItemDetailProjector.Project(
            LayoutWith(new LayoutControl("Custom.Thing", "Thing", "Contoso.WeirdWidget", false, true, false)),
            SnapshotWith(("Custom.Thing", "v"))));

        control.ControlType.ShouldBe("Contoso.WeirdWidget");
    }
}
