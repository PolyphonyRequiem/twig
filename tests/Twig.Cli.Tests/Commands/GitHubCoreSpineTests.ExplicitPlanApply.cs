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
        var file = Path.Combine(fixture.Root, "change.json");
        await fixture.UseWorkspace(async workspace =>
        {
            var prepared = await workspace.PreviewDraftAsync(item.Key, default);
            prepared.IsSuccess.ShouldBeTrue(prepared.IsSuccess ? "" : prepared.Error.Message);
            await File.WriteAllBytesAsync(file, Core.Core.PlanBytes(prepared.Value.Plan!));
        });
        fixture.Http.Writes.ShouldBe(0);

        fixture.Run("plan", "apply", "--file", file, "-o", "json").ShouldBe(0, fixture.Error);

        fixture.Http.Writes.ShouldBe(1);
        fixture.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Output.ShouldContain("Applied from the named file");
    }
}
