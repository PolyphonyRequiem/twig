using Shouldly;
using Twig.Core;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class PlanningTests
{
    private readonly Connection connection = new(new("ado"), "collection", "project", "actor");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-11-03T22:30:00Z");

    [Theory]
    [InlineData("@current", "@Current")]
    [InlineData("@CURRENT+2", "@Current+2")]
    [InlineData("@Current-1", "@Current-1")]
    [InlineData("Project\\Exact iteration", "Project\\Exact iteration")]
    public void ExpressionsPreserveAbsoluteIntentAndCanonicalizeRelativeIntent(string input, string expected)
        => new IterationExpression(input).Text.ShouldBe(expected);

    [Theory]
    [InlineData("@current+0")]
    [InlineData("@Current-0")]
    [InlineData("@unknown")]
    [InlineData("@current+2147483648")]
    public void InvalidExpressionsAreNotSilentlyBroadened(string text)
        => Should.Throw<ArgumentException>(() => new IterationExpression(text));

    [Fact]
    public void CachedCalendarResolvesEveryOverlappingCurrentAndOffsetWithoutBroaderFallback()
    {
        var calendar = new PlanningMetadata(now, [],
        [
            new("Project\\Earlier", now.AddDays(-4), now.AddDays(-2)),
            new("Project\\Current A", now.AddDays(-1), now),
            new("Project\\Current B", now, now.AddDays(1)),
            new("Project\\Next", now.AddDays(2), now.AddDays(3)),
            new("Project\\Undated", null, null),
        ], [], "Native team");
        var current = new Query(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current"]), planningTeam: "Native team");
        Required(Model.Matches(current, Item("project\\CURRENT a"), calendar, now)).ShouldBeTrue();
        Required(Model.Matches(current, Item("Project\\Current B"), calendar, now)).ShouldBeTrue();
        Required(Model.Matches(current, Item("Project\\Next"), calendar, now)).ShouldBeFalse();
        var next = new Query(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current+1"]), planningTeam: "Native team");
        Required(Model.Matches(next, Item("Project\\Current B"), calendar, now)).ShouldBeTrue();
        Required(Model.Matches(next, Item("Project\\Next"), calendar, now)).ShouldBeTrue();
        Required(Model.Matches(next, Item("Project\\Current A"), calendar, now)).ShouldBeFalse();
        Required(Model.Matches(new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["@Current+99"]), planningTeam: "Native team"),
            Item("Project\\Next"), calendar, now)).ShouldBeFalse();
        Required(Model.Matches(current, Item("Project\\Next"), calendar, now.AddYears(1))).ShouldBeFalse();
        Model.Matches(current, Item("Project\\Next")).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.Matches(current, Item("Project\\Next"), calendar with { Team = "Different team" }, now).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(new(Guid.NewGuid(), "Relative intent", [new(SelectorKind.Query, query: current)]),
            new(0), new(connection, [], []), now).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Required(Model.Matches(new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(["Project\\Next"])),
            Item("project\\next"))).ShouldBeTrue();
    }

    [Fact]
    public void AreaExactAndUnderUseCaseInsensitiveComponentBoundariesAndRulesUnionWithIndividualPins()
    {
        var a = Item("iteration", "project\\AREA");
        var b = Item("iteration", "Project\\Area\\Child", "43");
        var c = Item("iteration", "Project\\AreaOther", "44");
        var exact = new Query(QueryKind.PathEqual, "System.AreaPath", FieldValue.String("Project\\Area"));
        var under = new Query(QueryKind.Under, "System.AreaPath", FieldValue.String("Project\\Area"));
        Required(Model.Matches(exact, a)).ShouldBeTrue();
        Required(Model.Matches(exact, b)).ShouldBeFalse();
        Required(Model.Matches(under, b)).ShouldBeTrue();
        Required(Model.Matches(under, c)).ShouldBeFalse();
        var bench = new Bench(Guid.NewGuid(), "Rules",
            [new(SelectorKind.Query, query: under, connection: connection), new(SelectorKind.Pin, c.Key, connection: connection)]);
        Required(Model.ResolveBench(bench, new(0, [a, b, c]), new(connection, [], []))).Length.ShouldBe(3);
    }

    private WorkItem Item(string iteration, string area = "Project\\Area", string id = "42")
    {
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, id);
        return new(key, "Native.Type", FieldValues.Empty.Set("System.IterationPath", FieldValue.String(iteration))
            .Set("System.AreaPath", FieldValue.String(area)), new(key, "1"), true);
    }
    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }
}
