using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
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
        var handles = fixture.Native.Http.HandleCreations;
        var selected = await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture));
        selected.IsSuccess.ShouldBeTrue(selected.IsSuccess ? "" : $"{selected.Error.Code}: {selected.Error.Message}");
        (fixture.Native.Http.HandleCreations - handles).ShouldBe(2,
            "one source admission at entry and one after provider work; scalar helpers must not reopen the same admitted workspace");
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

    [Theory]
    [InlineData("actor")]
    [InlineData("bench")]
    [InlineData("mode")]
    public async Task NativeGitHubLocalAndScalarSelectionStillRefuseStaleAdmission(string damage)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Items.Length.ShouldBe(2);
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldBeEmpty();
        before.Journal.ShouldBeEmpty();
        if (damage == "actor") fixture.Defect = damage;
        else if (damage == "mode") await fixture.SetPlanAsync(true);
        else
        {
            var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
            var bench = (await user.ReadAsync()).Value.Benches.Single(value => value.Id == fixture.Host.Bench);
            (await user.SaveAsync(new(bench.Id, "changed", bench.Selectors), bench.Revision)).IsSuccess.ShouldBeTrue();
        }
        (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await editor.SelectAsync(fixture.Key)).Error.Code.ShouldBe(ErrorCode.Conflict);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalSelectionRefusesDebtIntroducedWhileReadingOtherEndpoint()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Items.Length.ShouldBe(2);
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldBeEmpty();
        before.Journal.ShouldBeEmpty();
        Draft? debt = null;
        fixture.Native.Http.BeforeIssueRead = async id =>
        {
            if (id != "I_other" || debt is not null) return;
            await fixture.Native.UseWorkspace(async workspace =>
            {
                var staged = await workspace.StageEditAsync(fixture.Key,
                    FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("independent debt")), default,
                    capturedBaseline: before.Items.Single(item => item.Key == fixture.Key));
                staged.IsSuccess.ShouldBeTrue();
                debt = staged.Value;
            });
        };
        var result = await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture));
        fixture.Native.Http.BeforeIssueRead = null;
        debt.ShouldNotBeNull();
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Conflict);
        var after = await fixture.SnapshotAsync();
        after.Drafts.ShouldHaveSingleItem().ShouldBe(debt);
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("mode")]
    [InlineData("store")]
    public async Task NativeGitHubLocalSelectionRechecksAfterCoherentProviderRead(string damage)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Items.Length.ShouldBe(2);
        before.Drafts.ShouldBeEmpty();
        before.Proposals.ShouldBeEmpty();
        before.Journal.ShouldBeEmpty();
        var reads = 0;
        fixture.Native.Http.BeforeIssueRead = async id =>
        {
            if (id != "I_opaque" || ++reads != 3) return;
            if (damage == "endpoint") fixture.Native.Http.OtherIssue!["updatedAt"] = "2026-11-08T12:00:00Z";
            else if (damage == "mode") await fixture.SetPlanAsync(true);
            else await fixture.Native.ReplaceItems(before.Items);
        };
        var result = await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture));
        fixture.Native.Http.BeforeIssueRead = null;
        reads.ShouldBe(4, "invalidate during the final pair read, after both scalar observations were mirrored");
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Conflict);
        var after = await fixture.SnapshotAsync();
        after.Drafts.ShouldBeEmpty();
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubProviderDesignDeclaresExactlyOneLocalOnlySymmetricAssociation()
    {
        using var fixture = new HumanEditorFixture();
        await fixture.InitializeAsync();
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
        fixture.Writes.ShouldBe(0);
    }
}
