using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Commands;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Commands;

public class AddNoteCommandTests
{
    [Fact]
    public void Execute_AddsNoteToWorkItem()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var note = new PendingNote("Review complete", DateTimeOffset.UtcNow, false);
        var cmd = new AddNoteCommand(note);

        cmd.Execute(wi);

        wi.PendingNotes.Count.ShouldBe(1);
        wi.PendingNotes[0].Text.ShouldBe("Review complete");
    }

    [Fact]
    public void Execute_HtmlNote_PreservesIsHtmlFlag()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var note = new PendingNote("<b>HTML note</b>", DateTimeOffset.UtcNow, IsHtml: true);
        var cmd = new AddNoteCommand(note);

        cmd.Execute(wi);

        wi.PendingNotes[0].IsHtml.ShouldBeTrue();
    }

    [Fact]
    public void ToFieldChange_AlwaysReturnsNull()
    {
        var note = new PendingNote("text", DateTimeOffset.UtcNow, false);
        var cmd = new AddNoteCommand(note);

        // Must return null regardless of execution state
        cmd.ToFieldChange().ShouldBeNull();

        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        cmd.Execute(wi);
        cmd.ToFieldChange().ShouldBeNull();
    }

    [Fact]
    public void Note_Property_IsPreserved()
    {
        var note = new PendingNote("my note", DateTimeOffset.UtcNow, false);
        var cmd = new AddNoteCommand(note);

        cmd.Note.ShouldBe(note);
    }
}

public class UpdateFieldCommandTests
{
    [Fact]
    public void Execute_SetsFieldValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var cmd = new UpdateFieldCommand("System.Description", "A description");

        cmd.Execute(wi);

        wi.Fields["System.Description"].ShouldBe("A description");
    }

    [Fact]
    public void ToFieldChange_BeforeExecute_OldValueIsNull()
    {
        var cmd = new UpdateFieldCommand("System.Description", "new value");

        var change = cmd.ToFieldChange();

        change.ShouldNotBeNull();
        change!.Value.FieldName.ShouldBe("System.Description");
        change.Value.OldValue.ShouldBeNull();
        change.Value.NewValue.ShouldBe("new value");
    }

    [Fact]
    public void ToFieldChange_AfterExecute_CapturesOldValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("System.Description", "old value");

        var cmd = new UpdateFieldCommand("System.Description", "new value");
        cmd.Execute(wi);
        var change = cmd.ToFieldChange();

        change.ShouldNotBeNull();
        change!.Value.FieldName.ShouldBe("System.Description");
        change.Value.OldValue.ShouldBe("old value");
        change.Value.NewValue.ShouldBe("new value");
    }

    [Fact]
    public void ToFieldChange_AfterExecute_FieldWasAbsent_OldValueIsNull()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var cmd = new UpdateFieldCommand("System.NewField", "value");

        cmd.Execute(wi);
        var change = cmd.ToFieldChange();

        change!.Value.OldValue.ShouldBeNull();
        change.Value.NewValue.ShouldBe("value");
    }

    [Fact]
    public void Execute_NullValue_SetsNullField()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("System.AssignedTo", "someone");

        var cmd = new UpdateFieldCommand("System.AssignedTo", null);
        cmd.Execute(wi);

        wi.Fields["System.AssignedTo"].ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_NullOrWhitespaceFieldName_Throws(string? fieldName)
    {
        Should.Throw<ArgumentException>(() => new UpdateFieldCommand(fieldName!, "value"));
    }
}
