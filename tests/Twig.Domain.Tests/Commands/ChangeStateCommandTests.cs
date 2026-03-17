using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Commands;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Commands;

public class ChangeStateCommandTests
{
    // ═══════════════════════════════════════════════════════════════
    //  ToFieldChange() — before Execute()
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When a command has never been executed, _oldState is null.
    /// ToFieldChange() should still return a valid FieldChange whose
    /// OldValue is null — this documents the single dequeue-execute-query
    /// flow contract: callers MUST call Execute() before ToFieldChange().
    /// </summary>
    [Fact]
    public void ToFieldChange_BeforeExecute_OldValueIsNull()
    {
        var cmd = new ChangeStateCommand("Active");

        var change = cmd.ToFieldChange();

        change.ShouldNotBeNull();
        change!.Value.FieldName.ShouldBe("System.State");
        change.Value.OldValue.ShouldBeNull();
        change.Value.NewValue.ShouldBe("Active");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ToFieldChange() — after Execute()
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToFieldChange_AfterExecute_CapturesOldState()
    {
        var wi = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Bug,
            Title = "Test",
            State = "New",
        };
        var cmd = new ChangeStateCommand("Active");

        cmd.Execute(wi);
        var change = cmd.ToFieldChange();

        change.ShouldNotBeNull();
        change!.Value.FieldName.ShouldBe("System.State");
        change.Value.OldValue.ShouldBe("New");
        change.Value.NewValue.ShouldBe("Active");
        wi.State.ShouldBe("Active");
    }
}
