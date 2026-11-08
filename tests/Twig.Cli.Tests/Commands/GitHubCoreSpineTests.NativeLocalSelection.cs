using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeGitHubPublishedPairQualifiesLocalOnlyAssociationWithoutScalarAuthority()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        editor.PlanMode.ShouldBeFalse();
        editor.StoreInstance.ShouldBe(fixture.Host.Stamp.StoreInstance);
        var before = await fixture.SnapshotAsync();
        before.Items.Length.ShouldBe(2);
        before.Items.ShouldAllBe(item => !item.Key.IsLocal && item.RelationshipsComplete && item.Revision != null);
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldBeEmpty();
        before.Journal.ShouldBeEmpty();
        var selected = await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture));
        selected.IsSuccess.ShouldBeTrue(selected.IsSuccess ? "" : $"{selected.Error.Code}: {selected.Error.Message}");
        selected.Value.Target.Key.ShouldBe(fixture.Key);
        selected.Value.Endpoint.Key.ShouldBe(LocalPairOther(fixture));
        selected.Value.Relations.ShouldHaveSingleItem().Shape.ShouldBe(RelationShape.Association);
        selected.Value.Relations.Single().Directed.ShouldBeFalse();
        selected.Value.Fields.Evaluation.Fields.ShouldAllBe(field => field.ReadOnly);
        selected.Value.Fields.RelationshipsReadOnly.ShouldBeTrue();
        fixture.Writes.ShouldBe(0);
        var after = await fixture.SnapshotAsync();
        after.Drafts.ShouldBeEmpty();
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
    }

    private static ItemKey LocalPairOther(HumanEditorFixture fixture)
        => ItemKey.Remote(fixture.Host.Stamp.Connection.Provider, fixture.Host.Stamp.Connection.ResourceScope, "I_other");

    private static async Task InitializeLocalPair(HumanEditorFixture fixture)
    {
        await fixture.InitializeAsync();
        fixture.Native.Http.OtherIssue = (System.Text.Json.Nodes.JsonObject)fixture.Native.Http.Issue.DeepClone();
        fixture.Native.Http.OtherIssue["id"] = "I_other";
        fixture.Native.Http.OtherIssue["number"] = 43;
        fixture.Native.Http.OtherIssue["title"] = "other";
        fixture.Native.Run("show", "43", "--refresh", "-o", "json").ShouldBe(0, fixture.Native.Error);
        await fixture.Native.UseWorkspace(async workspace =>
        {
            var schema = await workspace.SchemaAsync(default);
            schema.IsSuccess.ShouldBeTrue();
            schema.Value.Connection.ShouldBe(fixture.Host.Stamp.Connection);
            var association = schema.Value.Relations.Where(kind => kind.Shape == RelationShape.Association && !kind.Directed
                && workspace.Capabilities.LocalRelations.Contains(kind.Reference)
                && !workspace.Capabilities.NativeRelations.Contains(kind.Reference)).ToArray();
            association.ShouldHaveSingleItem();
            workspace.Capabilities.AtomicRelationshipBatches.ShouldBeFalse();
            workspace.Capabilities.AtomicFieldBatches.ShouldBeFalse();
        });
        fixture.Writes.ShouldBe(0);
    }
}
