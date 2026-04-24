using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class WorkspaceAreaPathTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var entry = new WorkspaceAreaPath(@"Project\TeamA", "under");

        entry.Path.ShouldBe(@"Project\TeamA");
        entry.Semantics.ShouldBe("under");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new WorkspaceAreaPath(@"Project\TeamA", "under");
        var b = new WorkspaceAreaPath(@"Project\TeamA", "under");
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentPath_AreNotEqual()
    {
        var a = new WorkspaceAreaPath(@"Project\TeamA", "under");
        var b = new WorkspaceAreaPath(@"Project\TeamB", "under");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentSemantics_AreNotEqual()
    {
        var a = new WorkspaceAreaPath(@"Project\TeamA", "exact");
        var b = new WorkspaceAreaPath(@"Project\TeamA", "under");
        a.ShouldNotBe(b);
    }
}
