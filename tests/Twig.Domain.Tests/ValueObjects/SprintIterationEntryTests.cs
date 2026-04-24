using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class SprintIterationEntryTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var entry = new SprintIterationEntry("@CurrentIteration", "relative");

        entry.Expression.ShouldBe("@CurrentIteration");
        entry.Type.ShouldBe("relative");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new SprintIterationEntry("@CurrentIteration", "relative");
        var b = new SprintIterationEntry("@CurrentIteration", "relative");
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentExpression_AreNotEqual()
    {
        var a = new SprintIterationEntry("@CurrentIteration", "relative");
        var b = new SprintIterationEntry("@NextIteration", "relative");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentType_AreNotEqual()
    {
        var a = new SprintIterationEntry("path", "relative");
        var b = new SprintIterationEntry("path", "absolute");
        a.ShouldNotBe(b);
    }
}
