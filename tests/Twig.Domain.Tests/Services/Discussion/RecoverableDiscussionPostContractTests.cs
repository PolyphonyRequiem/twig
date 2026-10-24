using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Discussion;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Discussion;

public sealed class RecoverableDiscussionPostContractTests
{
    [Fact]
    public void RecoverableDiscussionPostContract_ExistsForExactVersionedPayloadAndRecoveryState()
    {
        var domain = typeof(WorkItem).Assembly;
        var post = domain.GetType("Twig.Domain.Services.Discussion.RecoverableDiscussionPost");
        var receipt = domain.GetType("Twig.Domain.Services.Discussion.DiscussionPostReceipt");
        var state = domain.GetType("Twig.Domain.Services.Discussion.DiscussionPostState");
        var store = domain.GetType("Twig.Domain.Interfaces.IRecoverableDiscussionPostStore");

        post.ShouldNotBeNull();
        receipt.ShouldNotBeNull();
        state.ShouldNotBeNull();
        store.ShouldNotBeNull();
        post!.GetProperty("Version").ShouldNotBeNull();
        post.GetProperty("OperationId").ShouldNotBeNull();
        post!.GetProperty("ItemKey").ShouldNotBeNull();
        post.GetProperty("Text").ShouldNotBeNull();
        post.GetProperty("Format").ShouldNotBeNull();
        post.GetProperty("PayloadDigest").ShouldNotBeNull();
        post.GetProperty("Generation").ShouldNotBeNull();
        post.GetProperty("State").ShouldNotBeNull();
        post.GetProperty("Receipt").ShouldNotBeNull();
    }

    [Fact]
    public void PostPayloadDigest_BindsQualifiedIdentityExactTextAndFormat()
    {
        var operationId = Guid.NewGuid();
        var key = Key("binding-a");
        var post = RecoverableDiscussionPost.Create(
            operationId, key, DiscussionPostFormat.Markdown, "Exact **text**",
            DateTimeOffset.UnixEpoch);

        post.Version.ShouldBe(RecoverableDiscussionPost.CurrentVersion);
        post.OperationId.ShouldBe(operationId);
        post.ItemKey.ShouldBe(key);
        post.PayloadDigest.ShouldMatch("^[0-9a-f]{64}$");
        post.PayloadDigest.ShouldNotBe(RecoverableDiscussionPost.Create(
            Guid.NewGuid(), Key("binding-b"), DiscussionPostFormat.Markdown, "Exact **text**").PayloadDigest);
        post.PayloadDigest.ShouldNotBe(RecoverableDiscussionPost.Create(
            Guid.NewGuid(), key, DiscussionPostFormat.PlainText, "Exact **text**").PayloadDigest);
        post.PayloadDigest.ShouldNotBe(RecoverableDiscussionPost.Create(
            Guid.NewGuid(), key, DiscussionPostFormat.Markdown, "Exact text").PayloadDigest);
    }

    [Fact]
    public void UnknownDispatchOutcome_CanBeReconciledButNeverReturnedToDispatch()
    {
        var prepared = RecoverableDiscussionPost.Create(
            Guid.NewGuid(), Key("binding-a"), DiscussionPostFormat.PlainText, "Post once.");
        var dispatching = prepared.BeginDispatch();
        var indeterminate = dispatching.MarkIndeterminate();
        var confirmed = indeterminate.Confirm(new DiscussionPostReceipt(
            "comment-17", DateTimeOffset.UnixEpoch, DiscussionPostConfirmationSource.Readback));

        prepared.Generation.ShouldBe(0);
        dispatching.Generation.ShouldBe(1);
        indeterminate.Generation.ShouldBe(2);
        indeterminate.RequiresReconciliation.ShouldBeTrue();
        confirmed.Generation.ShouldBe(3);
        confirmed.State.ShouldBe(DiscussionPostState.Confirmed);
        confirmed.Receipt!.CommentId.ShouldBe("comment-17");
        confirmed.Receipt.Source.ShouldBe(DiscussionPostConfirmationSource.Readback);
        confirmed.RequiresReconciliation.ShouldBeFalse();

        Should.Throw<InvalidOperationException>(() => indeterminate.BeginDispatch());
        Should.Throw<InvalidOperationException>(() => confirmed.BeginDispatch());
        Should.Throw<InvalidOperationException>(() => prepared.MarkIndeterminate());
    }

    private static QualifiedWorkItemKey Key(string bindingId)
        => new(@"C:\repo", "connection", bindingId, 42);
}
