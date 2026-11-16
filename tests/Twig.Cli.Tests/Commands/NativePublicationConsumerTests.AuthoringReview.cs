using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task CurrentAuthoringReviewRequiresExactInventoryConfirmationAndRetainsProtectedDebt()
    {
        var native = await RelationshipWorkspaceAsync();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var pinned = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Current Bench",
            [new(SelectorKind.Pin, item: pinned, connection: native.Connection)]), 0)).Value;
        bench.Selectors.ShouldHaveSingleItem();
        bench.Connections.ShouldHaveSingleItem().ShouldBe(native.Connection);
        var reviewed = await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        var review = reviewed.Value;
        (await review.ConfirmAsync(review.Digest, review.Confirmation, false, true)).IsSuccess.ShouldBeFalse();
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, false)).IsSuccess.ShouldBeFalse();
        (await review.ConfirmAsync(review.Digest, "wrong statement", true, true)).IsSuccess.ShouldBeFalse();
        (await review.ConfirmAsync("wrong digest", review.Confirmation, true, true)).IsSuccess.ShouldBeFalse();

        var before = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var key = ItemKey.Local(Guid.NewGuid());
        var draft = new Draft(key, "Task", null, FieldValues.Empty.Set("System.Title", FieldValue.String("Protected")));
        (await native.Store.CommitAsync(native.Connection, before.Version, new(before.Version + 1,
            before.Items, before.Relationships, [draft], before.Proposals, before.Journal, before.Mappings, before.Benches), default))
            .IsSuccess.ShouldBeTrue();
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, true)).IsSuccess.ShouldBeFalse();
        review = (await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, ConfigureHumanEditor, default)).Value;
        (await review.ConfirmAsync(review.Digest, review.Confirmation, true, true)).IsSuccess.ShouldBeTrue();
        var retainedBench = (await user.ReadAsync()).Value.Benches.ShouldHaveSingleItem();
        BenchCodec.Serialize(retainedBench).ShouldBe(BenchCodec.Serialize(bench));
        var retainedDraft = (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.ShouldHaveSingleItem();
        CoreWorkspace.Fingerprint(native.Connection, retainedDraft).ShouldBe(CoreWorkspace.Fingerprint(native.Connection, draft));
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task AuthoringReviewRefusesMissingFreshBirthEvidenceWithoutAdoptionOrSchemaRepair()
    {
        var native = await RelationshipWorkspaceAsync();
        var metadata = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db");
        await CaptureSql(metadata, "ALTER TABLE meta DROP COLUMN editor_initialized;");
        var before = await File.ReadAllBytesAsync(metadata);
        var reviewed = await NativeAuthoringReview.ReviewAsync(native.Paths.StartDir, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeFalse();
        (await File.ReadAllBytesAsync(metadata)).ShouldBe(before);
        _transport.PatchAttempts.ShouldBe(0);
    }
}
