using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class AreaPathFilterTests
{
    // ── Under semantics (IncludeChildren = true) ───────────────────────

    [Fact]
    public void Matches_Under_ExactSamePath_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team A").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Under_ChildPath_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team A\SubTeam").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Under_DeeplyNestedChild_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team A\SubTeam\Feature").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Under_ParentPath_ReturnsFalse()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse("Project").Value;

        filter.Matches(candidate).ShouldBeFalse();
    }

    [Fact]
    public void Matches_Under_SiblingPath_ReturnsFalse()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team B").Value;

        filter.Matches(candidate).ShouldBeFalse();
    }

    [Fact]
    public void Matches_Under_PrefixButNotChild_ReturnsFalse()
    {
        // "Team Alpha" starts with "Team A" but is not under "Team A"
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team Alpha").Value;

        filter.Matches(candidate).ShouldBeFalse();
    }

    [Fact]
    public void Matches_Under_CaseInsensitive_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"project\team a", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"Project\Team A\SubTeam").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    // ── Exact semantics (IncludeChildren = false) ──────────────────────

    [Fact]
    public void Matches_Exact_SamePath_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: false);
        var candidate = AreaPath.Parse(@"Project\Team A").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Exact_ChildPath_ReturnsFalse()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: false);
        var candidate = AreaPath.Parse(@"Project\Team A\SubTeam").Value;

        filter.Matches(candidate).ShouldBeFalse();
    }

    [Fact]
    public void Matches_Exact_CaseInsensitive_ReturnsTrue()
    {
        var filter = new AreaPathFilter(@"project\team a", IncludeChildren: false);
        var candidate = AreaPath.Parse(@"Project\Team A").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    // ── SemanticsLabel / ToString ──────────────────────────────────────

    [Fact]
    public void SemanticsLabel_Under()
    {
        var filter = new AreaPathFilter("Project", IncludeChildren: true);
        filter.SemanticsLabel.ShouldBe("under");
    }

    [Fact]
    public void SemanticsLabel_Exact()
    {
        var filter = new AreaPathFilter("Project", IncludeChildren: false);
        filter.SemanticsLabel.ShouldBe("exact");
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var filter = new AreaPathFilter(@"Project\Team A", IncludeChildren: true);
        filter.ToString().ShouldBe(@"Project\Team A (under)");
    }

    // ── Single-segment paths ───────────────────────────────────────────

    [Fact]
    public void Matches_Under_SingleSegment_ExactMatch_ReturnsTrue()
    {
        var filter = new AreaPathFilter("MyProject", IncludeChildren: true);
        var candidate = AreaPath.Parse("MyProject").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Under_SingleSegment_ChildPath_ReturnsTrue()
    {
        var filter = new AreaPathFilter("MyProject", IncludeChildren: true);
        var candidate = AreaPath.Parse(@"MyProject\Team A").Value;

        filter.Matches(candidate).ShouldBeTrue();
    }
}
