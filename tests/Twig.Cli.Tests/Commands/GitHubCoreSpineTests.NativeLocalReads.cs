using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeGitHubLocalReaderRediscoversOriginalPlannedAndSettledReviewWithoutCachedReference()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        byte[] original;
        using (var first = (await fixture.OpenAsync()).Value)
        {
            var selected = (await first.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
            var created = (await first.ReviewLocalRelationshipAsync(selected.Fields.Ticket, true)).Value;
            original = Model.ProposalBytes(created.Proposal);
        }
        using var reopened = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Journal.Single().Status.ShouldBe(OpStatus.Planned);
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var captures = (await user.ReadEditorCapturesAsync(default)).Value;
        var requests = fixture.Native.Http.Requests;
        var credentials = fixture.Native.CredentialCalls;
        var loaded = await reopened.ReadLocalRelationshipReviewsAsync();
        loaded.IsSuccess.ShouldBeTrue(loaded.IsSuccess ? "" : loaded.Error.Message);
        var review = loaded.Value.ShouldHaveSingleItem();
        Model.ProposalBytes(review.Proposal).ShouldBe(original);
        review.CaptureId.ShouldBe(captures.Single().Id, "the test did not retain the first public review reference");
        review.Outcome.ShouldNotBeNull().Journal!.Status.ShouldBe(OpStatus.Planned);
        review.Outcome.Settled.ShouldBeFalse();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(captures.Single()).ShouldBeTrue();
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Native.CredentialCalls.ShouldBe(credentials);
        fixture.Writes.ShouldBe(0);

        var applied = await reopened.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        applied.Value.Journal!.Status.ShouldBe(OpStatus.Verified);
        using var settledEditor = (await fixture.OpenAsync()).Value;
        var settledBefore = await fixture.SnapshotAsync();
        requests = fixture.Native.Http.Requests;
        var settled = (await settledEditor.ReadLocalRelationshipReviewsAsync()).Value.ShouldHaveSingleItem();
        Model.ProposalBytes(settled.Proposal).ShouldBe(original);
        settled.CaptureId.ShouldBe(review.CaptureId);
        settled.Outcome!.Journal!.Status.ShouldBe(OpStatus.Verified);
        settled.Outcome.Settled.ShouldBeTrue();
        (await settledEditor.ApplyLocalRelationshipAsync(settled.CaptureId, settled.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        AssertSameLocalState(settledBefore, await fixture.SnapshotAsync());
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalReaderIncludesOriginalPreparingCaptureWithoutCreatingJournal()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var interrupted = false;
        using (var first = (await fixture.OpenAsync()).Value)
        {
            var selected = (await first.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
            fixture.Native.Http.BeforeSchema = async () =>
            {
                if ((await user.ReadEditorCapturesAsync(default)).Value.Any(capture => capture.LocalRelationship is not null))
                {
                    interrupted = true;
                    fixture.Defect = "schema";
                }
            };
            var refused = await first.ReviewLocalRelationshipAsync(selected.Fields.Ticket, true);
            refused.IsSuccess.ShouldBeFalse();
            refused.Error.Code.ShouldBe(ErrorCode.SchemaChanged);
            fixture.Native.Http.BeforeSchema = null;
        }
        interrupted.ShouldBeTrue("the failure must follow actual durable capture reservation, before retention");
        var original = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        original.Ready.ShouldBeFalse();
        using var reopened = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Proposals.ShouldBeEmpty();
        before.Journal.ShouldBeEmpty();
        var requests = fixture.Native.Http.Requests;
        var loaded = await reopened.ReadLocalRelationshipReviewsAsync();
        loaded.IsSuccess.ShouldBeTrue(loaded.IsSuccess ? "" : loaded.Error.Message);
        var review = loaded.Value.ShouldHaveSingleItem();
        Encoding.UTF8.GetString(Model.ProposalBytes(review.Proposal)).ShouldBe(original.IntentJson);
        review.Outcome.ShouldNotBeNull().Journal.ShouldBeNull();
        review.Outcome.Error.ShouldNotBeNull();
        review.Outcome.Settled.ShouldBeFalse();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(original).ShouldBeTrue();
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalReaderIncludesProtectedOriginalAttemptWithoutReplay()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        byte[] original;
        using (var first = (await fixture.OpenAsync()).Value)
        {
            var selected = (await first.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
            var created = (await first.ReviewLocalRelationshipAsync(selected.Fields.Ticket, true)).Value;
            original = Model.ProposalBytes(created.Proposal);
            var reads = 0;
            fixture.Native.Http.BeforeIssueRead = id =>
            {
                if (id == "I_other" && ++reads == 3)
                    fixture.Native.Http.OtherIssue!["updatedAt"] = "2026-11-09T12:00:00Z";
                return Task.CompletedTask;
            };
            var protectedResult = await first.ApplyLocalRelationshipAsync(created.CaptureId, created.Proposal.Digest);
            protectedResult.Value.Journal!.Status.ShouldBe(OpStatus.Applying);
            protectedResult.Value.Error.ShouldNotBeNull();
            fixture.Native.Http.BeforeIssueRead = null;
            reads.ShouldBe(4);
        }
        using var reopened = (await fixture.OpenAsync()).Value;
        var before = await fixture.SnapshotAsync();
        before.Relationships.ShouldBeEmpty();
        var requests = fixture.Native.Http.Requests;
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        var loaded = await reopened.ReadLocalRelationshipReviewsAsync();
        var review = loaded.Value.ShouldHaveSingleItem();
        Model.ProposalBytes(review.Proposal).ShouldBe(original);
        review.Outcome!.Journal!.Status.ShouldBe(OpStatus.Applying);
        review.Outcome.Journal.Attempt.ShouldBe(before.Journal.Single().Attempt);
        review.Outcome.Journal.Receipts.ShouldBeEmpty();
        review.Outcome.Error.ShouldNotBeNull();
        (await reopened.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(capture).ShouldBeTrue();
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalReaderDoesNotExposeAnotherBenchOrAdoptPlanMode()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var original = (await fixture.OpenAsync()).Value;
        var pair = (await original.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        (await original.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).IsSuccess.ShouldBeTrue();
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var otherBench = (await user.SaveAsync(new(Guid.NewGuid(), "unrelated owner", []), 0)).Value;
        using var foreign = (await fixture.OpenAsync(fixture.Host.Root, otherBench.Id, fixture.Host.Stamp, default)).Value;
        var before = await fixture.SnapshotAsync();
        var requests = fixture.Native.Http.Requests;
        (await foreign.ReadLocalRelationshipReviewsAsync()).Value.ShouldBeEmpty();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        fixture.Native.Http.Requests.ShouldBe(requests);
        await fixture.SetPlanAsync(true);
        (await original.ReadLocalRelationshipReviewsAsync()).IsSuccess.ShouldBeFalse("a stale normal handle cannot expose intent in Plan mode");
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("proposal")]
    public async Task NativeGitHubLocalReaderMissingOriginalProofRefusesInsteadOfEmptySuccess(string damage)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).IsSuccess.ShouldBeTrue();
        var file = damage == "capture" ? Path.Combine(Path.GetDirectoryName(fixture.Host.Registry)!, "benches.db") : fixture.Native.Database;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = damage == "capture" ? "DELETE FROM editor_captures;" : "DELETE FROM core_snapshots;";
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        var before = await fixture.SnapshotAsync();
        var requests = fixture.Native.Http.Requests;
        var result = await editor.ReadLocalRelationshipReviewsAsync();
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }
}
