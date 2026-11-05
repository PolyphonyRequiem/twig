using Shouldly;
using Twig.Core;
using Xunit;
using Provider = Twig.Providers.GitHub.GitHubProvider;

namespace Twig.Providers.GitHub.Tests;

public sealed partial class GitHubProviderTests
{
    [Fact]
    public async Task GitHubProviderCompoundRelationshipsNeverBecomeAFieldOnlyMutation()
    {
        using var fixture = await Fixture.Create();
        var source = await fixture.Item();
        await fixture.Item("I2");
        var proposal = await fixture.Proposal(new("compound", OpKind.Batch, source.Key, source.Revision,
            FieldValues.Empty.Set(Provider.Title, FieldValue.String("must not publish")),
            concurrency: ConcurrencyMode.ObservedNonAtomic, links: [new(fixture.Key("I2"), Provider.Dependency, true)]));
        (await fixture.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(Provider.Assignees)]
    [InlineData(Provider.Labels)]
    [InlineData(Provider.Milestone)]
    public async Task EditorChoicesRetainCanonicalRepositoryIdsWithoutRepeatedIntrospection(string field)
    {
        using var fixture = await Fixture.Create();
        var captured = await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default);
        captured.IsSuccess.ShouldBeTrue(captured.IsSuccess ? "" : captured.Error.Message);
        var before = fixture.Http.ReadQueries.Count;
        var choices = await fixture.Provider.EditorChoicesAsync(captured.Value, "T1", field, "Same display", 10, default);
        choices.IsSuccess.ShouldBeTrue(choices.IsSuccess ? "" : choices.Error.Message);
        choices.Value.Items.Single().Value.ShouldBe(field == Provider.Milestone
            ? FieldValue.Identity("N_canonical") : FieldValue.Strings(["N_canonical"]));
        fixture.Http.ReadQueries.Skip(before).ShouldAllBe(query => !query.Contains("__type(") && !query.Contains("issueTypes"));
        fixture.Http.Writes.ShouldBe(0);
        (await fixture.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("context")]
    [InlineData("repository")]
    [InlineData("denied")]
    [InlineData("invalid")]
    [InlineData("bounds")]
    [InlineData("foreign-capture")]
    public async Task EditorChoicesRefuseChangedActorResourceContextOrInvalidResults(string defect)
    {
        using var fixture = await Fixture.Create();
        var captured = (await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default)).Value;
        using var other = await Fixture.Create();
        if (defect == "actor") fixture.Access.Actor = fixture.Http.Actor = "A_other";
        else if (defect == "context") fixture.Access.Context = "other selected account";
        else fixture.Http.ChoiceDefect = defect;
        var choices = await fixture.Provider.EditorChoicesAsync(defect == "foreign-capture"
            ? (await other.Provider.CaptureEditorMetadataAsync(other.Connection, default)).Value : captured,
            "T1", Provider.Assignees, "query", defect == "bounds" ? 51 : 5, default);
        choices.IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
    }
}
