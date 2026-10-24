using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Discussion;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteRecoverableDiscussionPostStoreTests
{
    [Fact]
    public async Task JournalSurvivesReopen_AndDispatchCompareAndSwapPreventsReplayRace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-discussion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "twig.db");
        var key = Key("binding-a");
        var operationId = Guid.NewGuid();

        try
        {
            var created = RecoverableDiscussionPost.Create(
                operationId, key, DiscussionPostFormat.Markdown, "Keep this exact **text**.",
                DateTimeOffset.UnixEpoch);
            using (var cache = new SqliteCacheStore($"Data Source={databasePath}"))
            {
                IRecoverableDiscussionPostStore posts = new SqliteRecoverableDiscussionPostStore(cache);
                (await posts.TryCreateAsync(created)).ShouldBeTrue();
                (await posts.TryCreateAsync(created)).ShouldBeFalse();

                var firstDispatch = created.BeginDispatch();
                var competingDispatch = created.BeginDispatch();
                (await posts.TrySaveAsync(firstDispatch, created.Generation)).ShouldBeTrue();
                (await posts.TrySaveAsync(competingDispatch, created.Generation)).ShouldBeFalse();
            }

            using (var reopened = new SqliteCacheStore($"Data Source={databasePath}"))
            {
                IRecoverableDiscussionPostStore posts = new SqliteRecoverableDiscussionPostStore(reopened);
                var dispatching = await posts.GetAsync(operationId);
                dispatching.ShouldNotBeNull();
                dispatching!.State.ShouldBe(DiscussionPostState.Dispatching);
                dispatching.Text.ShouldBe("Keep this exact **text**.");
                dispatching.PayloadDigest.ShouldBe(created.PayloadDigest);
                dispatching.RequiresReconciliation.ShouldBeTrue();

                var indeterminate = dispatching.MarkIndeterminate();
                (await posts.TrySaveAsync(indeterminate, dispatching.Generation)).ShouldBeTrue();
                var unresolved = await posts.GetUnresolvedAsync(key);
                unresolved.ShouldHaveSingleItem().State.ShouldBe(DiscussionPostState.Indeterminate);

                var confirmed = indeterminate.Confirm(new DiscussionPostReceipt(
                    "comment-17", DateTimeOffset.UnixEpoch, DiscussionPostConfirmationSource.Readback));
                (await posts.TrySaveAsync(confirmed, indeterminate.Generation)).ShouldBeTrue();
                (await posts.GetUnresolvedAsync(key)).ShouldBeEmpty();

                var journal = await posts.GetJournalAsync(operationId);
                journal.Select(entry => entry.State).ShouldBe([
                    DiscussionPostState.Prepared,
                    DiscussionPostState.Dispatching,
                    DiscussionPostState.Indeterminate,
                    DiscussionPostState.Confirmed,
                ]);
                journal.Select(entry => entry.Generation).ShouldBe([0L, 1L, 2L, 3L]);
                (await posts.GetAsync(operationId))!.Receipt!.CommentId.ShouldBe("comment-17");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static QualifiedWorkItemKey Key(string bindingId)
        => new(@"C:\repo", "connection", bindingId, 42);
}
