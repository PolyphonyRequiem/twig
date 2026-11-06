using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    [Theory]
    [InlineData("truncated-folder")]
    [InlineData("changed-folder")]
    public async Task PlanningCatalogueDiscoveryRefusesIncompleteOrSubstitutedHierarchy(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.SourceDefect = defect;
        (await f.Provider.DiscoverPlanningSourcesAsync(f.Connection, "Selected team", default)).IsSuccess.ShouldBeFalse();
        f.Http.PlanningMembershipReads.ShouldBe(0);
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task PlanningCatalogueRetainsEmptyFoldersAndNativeExecutionRequirementsWithoutExecutingSources()
    {
        using var f = await Fixture.Create();
        var result = await f.Provider.DiscoverPlanningSourcesAsync(f.Connection, null, default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Team.ShouldBeNull();
        result.Value.Entries.ShouldContain(source => source.IsFolder && source.Path == "Shared Queries/Empty folder");
        result.Value.Entries.ShouldContain(source => source.Id == NativeHttp.FlatQuery && source.QueryType == "flat");
        result.Value.Gaps.ShouldNotBeEmpty();
        f.Http.PlanningMembershipReads.ShouldBe(0);
        f.Http.PlanningBatchSizes.ShouldBeEmpty();
        f.Http.ReadAccessPaths.ShouldAllBe(path => !path.Contains("/processes/") && !path.EndsWith("/fields")
            && !path.EndsWith("/layout") && !path.Contains("/workitemicons/"));
    }

    [Theory]
    [InlineData(PlanningSourceKind.SavedQuery)]
    [InlineData(PlanningSourceKind.Backlog)]
    public async Task PlanningSourceExecutionWithRecordedMappingUsesDirectIdentityAndOneRelationRosterWithoutSchemaCrawl(PlanningSourceKind kind)
    {
        using var f = await Fixture.Create();
        var schema = await f.Schema();
        schema.Types.Single().Rules.ShouldContain(rule => rule.Disabled
            && rule.Values.Any(value => value.DataType == "ado.native-type-name"));
        f.Http.ReadAccessPaths.Clear();
        var id = kind == PlanningSourceKind.SavedQuery ? NativeHttp.FlatQuery : "Native.Requirements";
        var result = await f.Provider.BrowsePlanningAsync(f.Connection, Fixture.Process, kind, id, Fixture.Process, schema, default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Source.Id.ShouldBe(id);
        result.Value.Team.ShouldBe(Fixture.Process);
        result.Value.Page.Items.Length.ShouldBe(kind == PlanningSourceKind.SavedQuery ? 1 : 2);
        result.Value.Page.Complete.ShouldBeTrue();
        result.Value.Page.Items.ShouldAllBe(item => item.RelationshipsComplete && item.ResourcesComplete);
        f.Http.ReadAccessPaths.Count(path => path.EndsWith("/workitemrelationtypes")).ShouldBe(1);
        f.Http.ReadAccessPaths.ShouldNotContain(path => path.EndsWith("/_apis/wit/queries") || path.EndsWith("/_apis/work/backlogs"));
        f.Http.ReadAccessPaths.ShouldAllBe(path => !path.Contains("/processes/") && !path.EndsWith("/fields")
            && !path.EndsWith("/states") && !path.EndsWith("/rules") && !path.EndsWith("/layout") && !path.Contains("/workitemicons/"));
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("unknown-relation")]
    [InlineData("foreign-schema")]
    public async Task PlanningSourceRecordedMappingRefusesUnknownOrForeignEvidenceWithoutPretendingEmpty(string defect)
    {
        using var f = await Fixture.Create();
        var schema = await f.Schema();
        if (defect == "unknown-type") f.Http.Items[2]["fields"]!["System.WorkItemType"] = "Unknown.Native.Type";
        if (defect == "unknown-relation")
            f.Http.Items[2]["relations"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"rel":"Unknown.Native.Relation","url":"https://example.test/organization/_apis/wit/workItems/1"}]""");
        if (defect == "foreign-schema") schema = new(new(new("ado"), Fixture.Collection, Fixture.Process, "foreign"), schema.Types, schema.Relations);
        var result = await f.Provider.BrowsePlanningAsync(f.Connection, Fixture.Process, PlanningSourceKind.SavedQuery,
            NativeHttp.FlatQuery, Fixture.Process, schema, default);
        result.IsSuccess.ShouldBeFalse();
        if (defect != "foreign-schema") result.Error.Message.ShouldContain("refresh native mapping metadata explicitly");
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }
}
