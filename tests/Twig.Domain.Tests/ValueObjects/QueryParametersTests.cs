using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class QueryParametersTests
{
    [Fact]
    public void Default_AllNullableProperties_AreNull()
    {
        var sut = new QueryParameters();

        sut.SearchText.ShouldBeNull();
        sut.TypeFilter.ShouldBeNull();
        sut.StateFilter.ShouldBeNull();
        sut.AssignedToFilter.ShouldBeNull();
        sut.AreaPathFilter.ShouldBeNull();
        sut.IterationPathFilter.ShouldBeNull();
        sut.CreatedSinceDays.ShouldBeNull();
        sut.ChangedSinceDays.ShouldBeNull();
        sut.DefaultAreaPaths.ShouldBeNull();
    }

    [Fact]
    public void Default_Top_Is25()
    {
        var sut = new QueryParameters();
        sut.Top.ShouldBe(25);
    }

    [Fact]
    public void InitProperties_RoundTrip()
    {
        var areaPaths = new List<(string Path, bool IncludeChildren)>
        {
            ("Project\\TeamA", true),
            ("Project\\TeamB", false),
        };

        var sut = new QueryParameters
        {
            SearchText = "login bug",
            TypeFilter = "Bug",
            StateFilter = "Active",
            AssignedToFilter = "Jane Doe",
            AreaPathFilter = "Project\\TeamA",
            IterationPathFilter = "Project\\Sprint 1",
            CreatedSinceDays = 7,
            ChangedSinceDays = 3,
            Top = 50,
            DefaultAreaPaths = areaPaths,
        };

        sut.SearchText.ShouldBe("login bug");
        sut.TypeFilter.ShouldBe("Bug");
        sut.StateFilter.ShouldBe("Active");
        sut.AssignedToFilter.ShouldBe("Jane Doe");
        sut.AreaPathFilter.ShouldBe("Project\\TeamA");
        sut.IterationPathFilter.ShouldBe("Project\\Sprint 1");
        sut.CreatedSinceDays.ShouldBe(7);
        sut.ChangedSinceDays.ShouldBe(3);
        sut.Top.ShouldBe(50);
        sut.DefaultAreaPaths.ShouldBe(areaPaths);
    }

    [Fact]
    public void With_CreatesModifiedCopy()
    {
        var original = new QueryParameters { Top = 10, TypeFilter = "Bug" };
        var modified = original with { Top = 100 };

        modified.Top.ShouldBe(100);
        modified.TypeFilter.ShouldBe("Bug");
        original.Top.ShouldBe(10);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new QueryParameters { SearchText = "test", Top = 10 };
        var b = new QueryParameters { SearchText = "test", Top = 10 };

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var a = new QueryParameters { SearchText = "test" };
        var b = new QueryParameters { SearchText = "other" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void DefaultAreaPaths_PreservesIncludeChildrenFlag()
    {
        var paths = new List<(string Path, bool IncludeChildren)>
        {
            ("Root\\Child", true),
            ("Root\\Other", false),
        };

        var sut = new QueryParameters { DefaultAreaPaths = paths };

        sut.DefaultAreaPaths![0].Path.ShouldBe("Root\\Child");
        sut.DefaultAreaPaths[0].IncludeChildren.ShouldBeTrue();
        sut.DefaultAreaPaths[1].Path.ShouldBe("Root\\Other");
        sut.DefaultAreaPaths[1].IncludeChildren.ShouldBeFalse();
    }

}
