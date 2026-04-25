using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class FieldChangeTests
{
    [Fact]
    public void Construction_SetsProperties()
    {
        var change = new FieldChange("System.State", "New", "Active");
        change.FieldName.ShouldBe("System.State");
        change.OldValue.ShouldBe("New");
        change.NewValue.ShouldBe("Active");
    }

    [Fact]
    public void Construction_NullOldValue()
    {
        var change = new FieldChange("System.Title", null, "My Title");
        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBe("My Title");
    }

    [Fact]
    public void Construction_NullNewValue()
    {
        var change = new FieldChange("System.AssignedTo", "user@test.com", null);
        change.OldValue.ShouldBe("user@test.com");
        change.NewValue.ShouldBeNull();
    }

    [Fact]
    public void Construction_BothNullValues()
    {
        var change = new FieldChange("System.Title", null, null);
        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBeNull();
    }

    [Fact]
    public void Equality_SameValues()
    {
        var a = new FieldChange("System.State", "New", "Active");
        var b = new FieldChange("System.State", "New", "Active");
        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentFieldName()
    {
        var a = new FieldChange("System.State", "New", "Active");
        var b = new FieldChange("System.Title", "New", "Active");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentOldValue()
    {
        var a = new FieldChange("System.State", "New", "Active");
        var b = new FieldChange("System.State", "Active", "Active");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentNewValue()
    {
        var a = new FieldChange("System.State", "New", "Active");
        var b = new FieldChange("System.State", "New", "Closed");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_BothNulls()
    {
        var a = new FieldChange("Field", null, null);
        var b = new FieldChange("Field", null, null);
        a.ShouldBe(b);
    }
}
