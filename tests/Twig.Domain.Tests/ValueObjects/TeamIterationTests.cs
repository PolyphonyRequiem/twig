using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class TeamIterationTests
{
    private static readonly DateTimeOffset Jan1 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan14 = new(2025, 1, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan15 = new(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Construction_SetsAllProperties()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);

        iteration.Path.ShouldBe(@"Project\Sprint 1");
        iteration.StartDate.ShouldBe(Jan1);
        iteration.EndDate.ShouldBe(Jan14);
    }

    [Fact]
    public void Construction_NullDates_Allowed()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", null, null);

        iteration.Path.ShouldBe(@"Project\Sprint 1");
        iteration.StartDate.ShouldBeNull();
        iteration.EndDate.ShouldBeNull();
    }

    [Fact]
    public void Construction_NullStartDate_Only()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", null, Jan14);

        iteration.StartDate.ShouldBeNull();
        iteration.EndDate.ShouldBe(Jan14);
    }

    [Fact]
    public void Construction_NullEndDate_Only()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", Jan1, null);

        iteration.StartDate.ShouldBe(Jan1);
        iteration.EndDate.ShouldBeNull();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);
        var b = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_BothNullDates_AreEqual()
    {
        var a = new TeamIteration(@"Project\Sprint 1", null, null);
        var b = new TeamIteration(@"Project\Sprint 1", null, null);

        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentPath()
    {
        var a = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);
        var b = new TeamIteration(@"Project\Sprint 2", Jan1, Jan14);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentStartDate()
    {
        var a = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);
        var b = new TeamIteration(@"Project\Sprint 1", Jan15, Jan14);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentEndDate()
    {
        var a = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);
        var b = new TeamIteration(@"Project\Sprint 1", Jan1, Jan15);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_NullVsNonNullStartDate()
    {
        var a = new TeamIteration(@"Project\Sprint 1", null, Jan14);
        var b = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void ToString_ContainsPath()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);

        iteration.ToString().ShouldContain(@"Project\Sprint 1");
    }

    [Fact]
    public void Deconstruction_Works()
    {
        var iteration = new TeamIteration(@"Project\Sprint 1", Jan1, Jan14);

        var (path, startDate, endDate) = iteration;

        path.ShouldBe(@"Project\Sprint 1");
        startDate.ShouldBe(Jan1);
        endDate.ShouldBe(Jan14);
    }
}
