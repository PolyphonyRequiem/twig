using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class QueryParametersTests
{
    [Fact]
    public void Default_PropertiesHaveExpectedDefaults()
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
        sut.Top.ShouldBe(25);
    }

}
