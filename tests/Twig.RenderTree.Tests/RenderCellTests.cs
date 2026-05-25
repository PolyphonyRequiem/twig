using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class RenderCellTests
{
    [Fact]
    public void Constructor_defaults_severity_to_None()
    {
        var cell = new RenderCell("hello", new RenderValue.String("hello"));
        cell.DisplayText.ShouldBe("hello");
        cell.Severity.ShouldBe(Severity.None);
    }

    [Fact]
    public void DisplayOnly_uses_Absent_for_machine_value()
    {
        var cell = RenderCell.DisplayOnly("●");
        cell.DisplayText.ShouldBe("●");
        cell.Value.ShouldBeOfType<RenderValue.Absent>();
    }

    [Fact]
    public void Integer_factory_mirrors_value_in_display_text_by_default()
    {
        var cell = RenderCell.Integer(42);
        cell.DisplayText.ShouldBe("42");
        ((RenderValue.Integer)cell.Value).Value.ShouldBe(42L);
    }

    [Fact]
    public void Integer_factory_respects_explicit_display_text()
    {
        var cell = RenderCell.Integer(42, "#42");
        cell.DisplayText.ShouldBe("#42");
        ((RenderValue.Integer)cell.Value).Value.ShouldBe(42L);
    }

    [Fact]
    public void Boolean_factory_renders_lowercase_true_false_by_default()
    {
        RenderCell.Boolean(true).DisplayText.ShouldBe("true");
        RenderCell.Boolean(false).DisplayText.ShouldBe("false");
    }

    [Fact]
    public void String_factory_mirrors_value_in_display_text()
    {
        var cell = RenderCell.String("Active");
        cell.DisplayText.ShouldBe("Active");
        ((RenderValue.String)cell.Value).Value.ShouldBe("Active");
    }

    [Fact]
    public void Severity_is_carried_through_factories()
    {
        RenderCell.String("oops", Severity.Error).Severity.ShouldBe(Severity.Error);
        RenderCell.Integer(0, severity: Severity.Warning).Severity.ShouldBe(Severity.Warning);
    }
}
