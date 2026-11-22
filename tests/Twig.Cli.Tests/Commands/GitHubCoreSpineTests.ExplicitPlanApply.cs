using Shouldly;
using Twig.Core;
using Twig.Providers.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task ExplicitPlanApplyDoesNotRequirePreviewOrConfirmation()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, fixture.Error);
        await fixture.Stage(GitHubProvider.Title, FieldValue.String("Applied from the named file"));
        var item = (await fixture.Snapshot()).Items.Single();
        var file = await WriteDraftPlan(fixture, item.Key);
        fixture.Http.Writes.ShouldBe(0);

        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(0, fixture.Error);

        fixture.Http.Writes.ShouldBe(1);
        (await fixture.Snapshot()).RetainedItems.ShouldBeEmpty();
        fixture.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Output.ShouldContain("Applied from the named file");
    }

    [Fact]
    public async Task ExplicitPlanApplyDoesNotSettleLaterTypeIntentWithTheSameFieldsAndBaseline()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, fixture.Error);
        var baseline = (await fixture.Snapshot()).Items.Single();
        var fields = FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("Same title intent"));
        await fixture.Stage(GitHubProvider.Title, fields[GitHubProvider.Title]);
        var file = await WriteDraftPlan(fixture, baseline.Key);
        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(0, fixture.Error);
        // Retain independent local type intent on the original edit baseline, without authorizing a provider type change.
        await fixture.UseWorkspace(async workspace =>
        {
            var staged = await workspace.StageEditAsync(baseline.Key, fields, default,
                typeOverride: "Later type intent", capturedBaseline: baseline);
            staged.IsSuccess.ShouldBeTrue();
        });
        var later = (await fixture.Snapshot()).RetainedItems.ShouldHaveSingleItem();

        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(0, fixture.Error);

        fixture.Http.Writes.ShouldBe(1);
        (await fixture.Snapshot()).RetainedItems.ShouldHaveSingleItem().ShouldBe(later);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSeedPlanApplySettlesOnlyVerifiedCreationAndNeverReplays(bool uncertain)
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        fixture.Run("seed", "new", "--title", "Named seed", "--type", GitHubProvider.Untyped, "--no-parent", "-o", "json")
            .ShouldBe(0, fixture.Error);
        var seed = (await fixture.Snapshot()).RetainedItems.ShouldHaveSingleItem();
        var file = await WriteDraftPlan(fixture, seed.Key);
        fixture.Http.LoseAcknowledgement = uncertain;
        var expected = uncertain ? 1 : 0;

        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(expected, fixture.Error);
        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(expected, fixture.Error);

        fixture.Http.Creates.ShouldBe(1);
        var snapshot = await fixture.Snapshot();
        var row = snapshot.Journal.ShouldHaveSingleItem();
        row.Status.ShouldBe(uncertain ? OpStatus.Indeterminate : OpStatus.Verified);
        if (uncertain)
        {
            snapshot.RetainedItems.ShouldHaveSingleItem().ShouldBe(seed);
            snapshot.Mappings.ShouldBeEmpty();
        }
        else
        {
            snapshot.RetainedItems.ShouldBeEmpty();
            var mapping = snapshot.Mappings.ShouldHaveSingleItem();
            mapping.Local.ShouldBe(seed.Key);
            mapping.PlanDigest.ShouldBe(row.Digest);
        }
    }

    private static async Task<string> WriteDraftPlan(Fixture fixture, ItemKey key)
    {
        var file = Path.Combine(fixture.Root, "change.json");
        await fixture.UseWorkspace(async workspace =>
        {
            var prepared = await workspace.PreviewDraftAsync(key, default);
            prepared.IsSuccess.ShouldBeTrue(prepared.IsSuccess ? "" : prepared.Error.Message);
            await File.WriteAllBytesAsync(file, Core.Core.PlanBytes(prepared.Value.Plan!));
        });
        return file;
    }
}
