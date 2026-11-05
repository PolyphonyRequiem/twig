using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGitHubEditorCapturesRemainScopedCachedAndModeGuarded(bool plan)
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        await fixture.UseEditor(plan, async (editor, user, bench) =>
        {
            var schemaCalls = fixture.Http.SchemaRequests;
            var key = ItemKey.Remote(editor.Origin.Connection.Provider, editor.Origin.Connection.ResourceScope, "I_opaque");
            var item = await editor.SelectAsync(key, default);
            item.IsSuccess.ShouldBeTrue(item.IsSuccess ? "" : item.Error.Message);
            var staged = await editor.StageAsync(item.Value,
                FieldValues.Empty.Set("github.title", FieldValue.String("durable scoped title")), null, null, default);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            fixture.Http.SchemaRequests.ShouldBe(schemaCalls);
            fixture.Http.Writes.ShouldBe(0);
            (await fixture.Snapshot()).Drafts.Length.ShouldBe(plan ? 0 : 1);
            (await editor.Workspace.SnapshotAsync(default)).Value.Drafts.Single().Changes["github.title"].Text.ShouldBe("durable scoped title");
            var library = (await user.ReadAsync()).Value;
            (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, editor.Plan.Revision, !plan)).IsSuccess.ShouldBeTrue();
            (await editor.StageAsync(staged.Value, FieldValues.Empty, null, null, default)).IsSuccess.ShouldBeFalse();
            fixture.Http.Writes.ShouldBe(0);
            (await user.ReadEditorCapturesAsync(default)).Value.Single().Settled.ShouldBeFalse();
        });
    }
}
