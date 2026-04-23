using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class WorkspaceModeTests
{
    [Theory]
    [InlineData("Sprint")]
    [InlineData("Area")]
    [InlineData("Recent")]
    public void TryParse_KnownMode_ReturnsInstance(string value)
    {
        var result = WorkspaceMode.TryParse(value);
        result.ShouldNotBeNull();
        result.Value.ShouldBe(value);
    }

    [Fact]
    public void TryParse_UnknownMode_ReturnsNull()
    {
        WorkspaceMode.TryParse("Unknown").ShouldBeNull();
    }

    [Fact]
    public void TryParse_Null_ReturnsNull()
    {
        WorkspaceMode.TryParse(null).ShouldBeNull();
    }

    [Fact]
    public void TryParse_Empty_ReturnsNull()
    {
        WorkspaceMode.TryParse("").ShouldBeNull();
    }

    [Fact]
    public void TryParse_CaseSensitive_LowercaseReturnsNull()
    {
        WorkspaceMode.TryParse("sprint").ShouldBeNull();
    }

    [Fact]
    public void StaticInstances_HaveCorrectValues()
    {
        WorkspaceMode.Sprint.Value.ShouldBe("Sprint");
        WorkspaceMode.Area.Value.ShouldBe("Area");
        WorkspaceMode.Recent.Value.ShouldBe("Recent");
    }

    [Fact]
    public void TryParse_Sprint_ReturnsSameInstance()
    {
        var parsed = WorkspaceMode.TryParse("Sprint");
        parsed.ShouldBeSameAs(WorkspaceMode.Sprint);
    }

    [Fact]
    public void TryParse_Area_ReturnsSameInstance()
    {
        var parsed = WorkspaceMode.TryParse("Area");
        parsed.ShouldBeSameAs(WorkspaceMode.Area);
    }

    [Fact]
    public void TryParse_Recent_ReturnsSameInstance()
    {
        var parsed = WorkspaceMode.TryParse("Recent");
        parsed.ShouldBeSameAs(WorkspaceMode.Recent);
    }

    [Fact]
    public void Equality_SameMode_AreEqual()
    {
        WorkspaceMode.Sprint.ShouldBe(WorkspaceMode.Sprint);
    }

    [Fact]
    public void Equality_DifferentModes_AreNotEqual()
    {
        WorkspaceMode.Sprint.ShouldNotBe(WorkspaceMode.Area);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        WorkspaceMode.Sprint.ToString().ShouldBe("Sprint");
    }
}
