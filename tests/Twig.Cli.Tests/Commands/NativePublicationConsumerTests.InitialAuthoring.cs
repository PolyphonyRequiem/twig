using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialReadonlyAuthoringAdoptionRequiresExactConfirmationAndRejectsCaptureEvidence(bool everCaptured)
    {
        var native = await RelationshipWorkspaceAsync();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Preserved prior readonly Bench", []), 0)).Value;
        var before = (await user.ReadAsync()).Value;
        var coreBefore = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var metadata = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db");
        await CaptureSql(metadata, """
            ALTER TABLE meta DROP COLUMN editor_registry;
            ALTER TABLE meta DROP COLUMN editor_generation;
            ALTER TABLE meta DROP COLUMN editor_head;
            """);
        if (everCaptured) await CaptureSql(metadata, "ALTER TABLE meta ADD COLUMN editor_initialized INTEGER NOT NULL DEFAULT 1;");
        await CaptureSql(_bindings.RegistryPath, """
            DROP TABLE editor_capture_refs; DROP TABLE editor_user_heads; DROP TABLE editor_provenance_meta;
            UPDATE layout_meta SET version=11;
            """);
        var wrong = await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, "unknown readonly history",
            NativeAuthoringReview.NativeBuild, "synthetic-new-panel-source", ConfigureHumanEditor, default);
        wrong.IsSuccess.ShouldBeFalse();
        var reviewed = await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, NativeInitialAuthoring.ReadOnlyRelease,
            NativeAuthoringReview.NativeBuild, "synthetic-new-panel-source", ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBe(!everCaptured, reviewed.IsSuccess ? "" : reviewed.Error.Message);
        if (everCaptured) return;
        var review = reviewed.Value;
        (await review.ConfirmAsync(review.Digest, review.Confirmation, false, true)).IsSuccess.ShouldBeFalse();
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, false)).IsSuccess.ShouldBeFalse();
        (await review.ConfirmAsync(review.Digest, "wrong statement", true, true)).IsSuccess.ShouldBeFalse();
        (await user.SaveAsync(new(bench.Id, "Preserved changed Bench", []), bench.Revision)).IsSuccess.ShouldBeTrue();
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, true)).IsSuccess.ShouldBeFalse();
        bench = (await user.ReadAsync()).Value.Benches.Single();
        before = (await user.ReadAsync()).Value;
        review = (await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, NativeInitialAuthoring.ReadOnlyRelease,
            NativeAuthoringReview.NativeBuild, "synthetic-new-panel-source", ConfigureHumanEditor, default)).Value;
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, true)).IsSuccess.ShouldBeTrue();
        var after = (await user.ReadAsync()).Value;
        after.InstanceId.ShouldBe(before.InstanceId);
        after.Revision.ShouldBe(before.Revision);
        after.Benches.Single().ShouldBe(bench);
        (await native.Store.ReadAsync(native.Connection, default)).Value.ShouldBe(coreBefore);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
        _transport.PatchAttempts.ShouldBe(0);
    }
}
