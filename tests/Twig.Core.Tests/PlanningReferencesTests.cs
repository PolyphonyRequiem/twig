using Shouldly;
using Twig.Core;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class PlanningReferencesTests
{
    [Fact]
    public void ReferenceRulesUseOnlyCompleteExactSourceTeamMembershipAndNeverTreatMissingContextItemsAsEmpty()
    {
        var connection = new Connection(new("ado"), "collection", "project", "actor");
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var item = new WorkItem(key, "Native.Type", FieldValues.Empty, new(key, "1"), true);
        var reference = new Query(QueryKind.SavedQuery, value: FieldValue.String("native.query"), planningTeam: "team-a");
        var bench = new Bench(Guid.NewGuid(), "Reference intent", [new(SelectorKind.Query, query: reference, connection: connection)]);
        var schema = new ProcessSchema(connection, [], []);
        var membership = new PlanningMembership(PlanningSourceKind.SavedQuery, "native.query", "team-a", [key], DateTimeOffset.UtcNow, true);
        var complete = schema with { Memberships = [membership] };
        Model.ResolveBench(bench, new(0, [item]), complete).Value.ShouldBe([key]);
        complete.Fingerprint.ShouldBe(schema.Fingerprint, "derived membership is not mutation/schema authority");
        Model.ResolveBench(bench, new(0, [item]), schema).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(bench, new(0), complete).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(bench, new(0, [item]), schema with { Memberships = [membership with { Team = "team-b" }] })
            .Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(bench, new(0, [item]), schema with { Memberships = [membership with { Kind = PlanningSourceKind.Backlog }] })
            .Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(bench, new(0, [item]), schema with { Memberships = [membership with { Complete = false }] })
            .Error.Code.ShouldBe(ErrorCode.Unavailable);
        Model.ResolveBench(bench, new(0, [item]), schema with { Memberships = [membership with { Items = [] }] })
            .Value.ShouldBeEmpty("a complete explicitly acquired empty result is distinct from missing membership");
        reference.Value!.Text.ShouldBe("native.query");
        reference.Children.ShouldBeEmpty();
    }
}
