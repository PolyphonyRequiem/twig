using Shouldly;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    public async Task PlanningTeamRosterExhaustsNativeOffsetPagesAndRetainsCanonicalIdentityAndActualDisplayName(int count)
    {
        using var f = await Fixture.Create();
        f.Http.TeamRosterCount = count;
        var result = await f.Provider.DiscoverPlanningTeamsAsync(f.Connection, default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Length.ShouldBe(count);
        if (count > 0)
        {
            result.Value[0].Id.ShouldBe("00000000-0000-0000-0000-000000000001");
            result.Value[0].Name.ShouldBe("Actual display / not ID");
            result.Value.Select(team => team.Id).Distinct().Count().ShouldBe(count);
        }
        f.Http.TeamRosterSkips.ShouldBe(count < 100 ? [0] : [0, 100]);
        f.Http.Writes.ShouldBe(0);
        f.Http.WorkItemReads.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("invalid-id")]
    [InlineData("missing-name")]
    [InlineData("denied")]
    [InlineData("unsupported-continuation")]
    [InlineData("over-bound")]
    public async Task PlanningTeamRosterRefusesMalformedDeniedRepeatedOrUnboundedDiscoveryWithoutPartialSuccess(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.TeamRosterDefect = defect;
        if (defect == "over-bound") f.Http.TeamRosterCount = 4100;
        var result = await f.Provider.DiscoverPlanningTeamsAsync(f.Connection, default);
        result.IsSuccess.ShouldBeFalse();
        if (defect == "denied") result.Error.Evidence.ShouldContain("http:403");
        f.Http.TeamRosterSkips.Count.ShouldBeLessThanOrEqualTo(41);
        f.Http.Writes.ShouldBe(0);
        f.Http.WorkItemReads.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }
}
