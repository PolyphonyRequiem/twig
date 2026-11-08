using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
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
        await AssertExactLocalRead(fixture, reopened, review);
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
        await AssertExactLocalRead(fixture, settledEditor, settled);
        AssertLocalOutcome(settled.Outcome,
            (await settledEditor.ObserveLocalRelationshipAsync(settled.CaptureId, settled.Proposal.Digest)).Value);
        AssertLocalOutcome(settled.Outcome,
            (await settledEditor.ContinueLocalRelationshipAsync(settled.CaptureId, settled.Proposal.Digest)).Value);
        await AssertExactLocalRead(fixture, settledEditor, settled);
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
        await AssertExactLocalRead(fixture, reopened, review);
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
        await AssertExactLocalRead(fixture, reopened, review);
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
        var created = await original.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true);
        created.IsSuccess.ShouldBeTrue();
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var otherBench = (await user.SaveAsync(new(Guid.NewGuid(), "unrelated owner", []), 0)).Value;
        using var foreign = (await fixture.OpenAsync(fixture.Host.Root, otherBench.Id, fixture.Host.Stamp, default)).Value;
        var before = await fixture.SnapshotAsync();
        var requests = fixture.Native.Http.Requests;
        (await foreign.ReadLocalRelationshipReviewsAsync()).Value.ShouldBeEmpty();
        (await foreign.ReadLocalRelationshipReviewAsync(created.Value.CaptureId, created.Value.Proposal.Digest))
            .Error.Code.ShouldBe(ErrorCode.Conflict);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        fixture.Native.Http.Requests.ShouldBe(requests);
        await fixture.SetPlanAsync(true);
        (await original.ReadLocalRelationshipReviewsAsync()).IsSuccess.ShouldBeFalse("a stale normal handle cannot expose intent in Plan mode");
        (await original.ReadLocalRelationshipReviewAsync(created.Value.CaptureId, created.Value.Proposal.Digest))
            .IsSuccess.ShouldBeFalse();
        using var plan = (await fixture.OpenAsync()).Value;
        plan.PlanMode.ShouldBeTrue();
        (await plan.ReadLocalRelationshipReviewAsync(created.Value.CaptureId, created.Value.Proposal.Digest))
            .Error.Code.ShouldBe(ErrorCode.Unsupported);
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
        var created = await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true);
        created.IsSuccess.ShouldBeTrue();
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
        var exact = await editor.ReadLocalRelationshipReviewAsync(created.Value.CaptureId, created.Value.Proposal.Digest);
        exact.IsSuccess.ShouldBeFalse();
        exact.Error.Code.ShouldBe(result.Error.Code);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalExactReaderRefusesInvalidTargetsAndReleasesCancelledOrContendedLeases()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var created = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var review = (await editor.ReadLocalRelationshipReviewsAsync()).Value.ShouldHaveSingleItem();
        review.CaptureId.ShouldBe(created.CaptureId);
        var before = await fixture.SnapshotAsync();
        var proof = await LocalCaptureProof(fixture);
        var requests = fixture.Native.Http.Requests;
        var credentials = fixture.Native.CredentialCalls;
        foreach (var digest in new[] { null!, "", " \t" })
            (await editor.ReadLocalRelationshipReviewAsync(review.CaptureId, digest)).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        (await editor.ReadLocalRelationshipReviewAsync(Guid.Empty, review.Proposal.Digest)).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Native.CredentialCalls.ShouldBe(credentials);
        review.Proposal.Digest.ToUpperInvariant().ShouldNotBe(review.Proposal.Digest);
        foreach (var digest in new[] { "not-a-digest", new string('0', 64),
            review.Proposal.Digest.ToUpperInvariant(), " " + review.Proposal.Digest, review.Proposal.Digest + " " })
            (await editor.ReadLocalRelationshipReviewAsync(review.CaptureId, digest)).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        (await editor.ReadLocalRelationshipReviewAsync(Guid.NewGuid(), review.Proposal.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            editor.ReadLocalRelationshipReviewAsync(review.CaptureId, review.Proposal.Digest, cancelled.Token));

        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        using (ConnectionOperationGate.Acquire(fixture.Host.Root, exclusive: true))
        {
            Task<Result<NativeLocalRelationshipReview>> contended;
            using (ExecutionContext.SuppressFlow())
                contended = Task.Run(() => editor.ReadLocalRelationshipReviewAsync(review.CaptureId, review.Proposal.Digest));
            await Assert.ThrowsAsync<ConnectionBindingResolutionException>(() => contended);
            using var releasedBench = user.AcquireAuthoringLease(fixture.Host.Bench);
        }
        await AssertExactLocalRead(fixture, editor, review);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Writes.ShouldBe(0);

        var observed = (await editor.ObserveLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value;
        observed.Journal!.Status.ShouldBe(OpStatus.Planned);
        observed.Error.ShouldBeNull();
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
        capture.Ready.ShouldBeTrue();
        capture.Settled.ShouldBeFalse();
        var bench = (await user.ReadAsync()).Value.Benches.Single(value => value.Id == fixture.Host.Bench);
        var changedBench = (await user.SaveAsync(new(bench.Id, "new revision between calls", bench.Selectors), bench.Revision)).Value;
        var changedCapture = await user.SaveEditorCaptureAsync(capture with
            { Revision = capture.Revision + 1, BenchRevision = changedBench.Revision }, capture.Revision, default);
        changedCapture.IsSuccess.ShouldBeTrue(changedCapture.IsSuccess ? "" : changedCapture.Error.Message);
        changedCapture.Value.Revision.ShouldBe(capture.Revision + 1);
        changedCapture.Value.IntentJson.ShouldBe(capture.IntentJson);
        var changedProof = await LocalCaptureProof(fixture);
        (await editor.ReadLocalRelationshipReviewAsync(review.CaptureId, review.Proposal.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await editor.ReadLocalRelationshipReviewsAsync()).Error.Code.ShouldBe(ErrorCode.Conflict);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(changedProof);
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalExactReaderEvaluatesOnlyNamedOriginalWhileAllRetainsOrderedRefusals()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var first = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        (await editor.ApplyLocalRelationshipAsync(first.CaptureId, first.Proposal.Digest)).Value.Settled.ShouldBeTrue();
        pair = (await editor.SelectRelationshipsAsync(LocalPairOther(fixture), fixture.Key)).Value;
        var second = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, false)).Value;
        (await editor.ApplyLocalRelationshipAsync(second.CaptureId, second.Proposal.Digest)).Value.Settled.ShouldBeTrue();
        var state = await fixture.SnapshotAsync();
        state.Relationships.ShouldHaveSingleItem().LocalVersion.ShouldBe(2);
        state.Relationships.Single().Deleted.ShouldBeTrue();
        state.Journal.Length.ShouldBe(2);
        state.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
        await fixture.Native.UseWorkspace(async workspace =>
        {
            (await workspace.StageEditAsync(fixture.Key, FieldValues.Empty.Set(Twig.Providers.GitHub.GitHubProvider.Title,
                FieldValue.String("independent scalar debt")), default)).IsSuccess.ShouldBeTrue();
        });
        (await fixture.SnapshotAsync()).Drafts.ShouldHaveSingleItem();
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var captures = (await user.ReadEditorCapturesAsync(default)).Value;
        captures.Length.ShouldBe(2);
        captures.ShouldAllBe(capture => capture.Ready && capture.Settled && capture.BenchId == fixture.Host.Bench);
        var proof = await LocalCaptureProof(fixture);
        var all = (await editor.ReadLocalRelationshipReviewsAsync()).Value;
        all.Select(review => review.CaptureId).ShouldBe(captures.Select(capture => capture.Id));
        await AssertExactLocalRead(fixture, editor, all.Single(review => review.CaptureId == first.CaptureId));
        await AssertExactLocalRead(fixture, editor, all.Single(review => review.CaptureId == second.CaptureId));
        (await LocalCaptureProof(fixture)).ShouldBe(proof);

        // Remove only the first original's Core proof; its native immutable history remains genuine.
        var damaged = all[0];
        var untouched = all[1];
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Native.Database, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT document FROM core_snapshots;";
            var document = System.Text.Json.Nodes.JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
            foreach (var name in new[] { "Proposals", "Journal" })
            {
                var rows = document[name]!.AsArray();
                var original = rows.Single(row => row!["Digest"]!.GetValue<string>() == damaged.Proposal.Digest);
                rows.Remove(original).ShouldBeTrue();
            }
            command.CommandText = "UPDATE core_snapshots SET document=$document;";
            command.Parameters.AddWithValue("$document", document.ToJsonString());
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
            transaction.Commit();
        }
        var damagedState = await fixture.SnapshotAsync();
        damagedState.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(untouched.Proposal.Digest);
        damagedState.Journal.ShouldHaveSingleItem().Digest.ShouldBe(untouched.Proposal.Digest);
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        await AssertExactLocalRead(fixture, editor, untouched);
        var refused = await editor.ReadLocalRelationshipReviewAsync(damaged.CaptureId, damaged.Proposal.Digest);
        refused.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        var refusedAll = await editor.ReadLocalRelationshipReviewsAsync();
        refusedAll.Error.ShouldBe(refused.Error);
        AssertSameLocalState(damagedState, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("repository")]
    [InlineData("bench")]
    [InlineData("mode")]
    [InlineData("store")]
    [InlineData("execution-path")]
    [InlineData("cancelled-provenance")]
    [InlineData("endpoint-provenance")]
    [InlineData("revision-provenance")]
    [InlineData("history")]
    public async Task NativeGitHubLocalExactReaderRevalidatesPersistedOriginalAfterVerifiedOperation(string damage)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var created = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var applied = (await editor.ApplyLocalRelationshipAsync(created.CaptureId, created.Proposal.Digest)).Value;
        applied.Settled.ShouldBeTrue();
        applied.Journal!.Status.ShouldBe(OpStatus.Verified);
        var review = (await editor.ReadLocalRelationshipReviewsAsync()).Value.ShouldHaveSingleItem();
        await AssertExactLocalRead(fixture, editor, review);
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
        capture.Settled.ShouldBeTrue();
        capture.Revision.ShouldBeGreaterThan(2);
        if (damage == "bench")
        {
            var bench = (await user.ReadAsync()).Value.Benches.Single(value => value.Id == fixture.Host.Bench);
            (await user.SaveAsync(new(bench.Id, "changed after effect", bench.Selectors), bench.Revision)).IsSuccess.ShouldBeTrue();
        }
        else if (damage == "mode") await fixture.SetPlanAsync(true);
        else if (damage is "actor" or "repository" or "store" or "execution-path")
        {
            var path = Path.Combine(fixture.Host.Root, ".twig", "core-workspace.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            document["principalId"]!.GetValue<string>().ShouldBe("U_actor");
            document["connection"]!["resourceScope"]!.GetValue<string>().ShouldBe(fixture.Host.Stamp.Connection.ResourceScope);
            document["connection"]!["destination"]!.GetValue<string>().ShouldBe(Twig.Providers.GitHub.GitHubProvider.Destination("O_owner", "R_repo"));
            if (damage == "actor") document["principalId"] = "U_other";
            else if (damage == "repository") document["connection"]!["destination"] = Twig.Providers.GitHub.GitHubProvider.Destination("O_owner", "R_other");
            else if (damage == "store") document["databaseInstance"] = Guid.NewGuid();
            else document["databasePath"] = Path.Combine(fixture.Host.Root, "unavailable-original.db");
            await File.WriteAllTextAsync(path, document.ToJsonString());
        }
        else if (damage.EndsWith("-provenance", StringComparison.Ordinal) || damage == "history")
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Path.GetDirectoryName(fixture.Host.Registry)!, "benches.db"), Pooling = false }.ToString());
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = damage switch
            {
                "cancelled-provenance" => "UPDATE editor_captures SET document=json_set(document,'$.Cancelled',json('true'));",
                "endpoint-provenance" => "UPDATE editor_captures SET document=json_set(document,'$.Endpoints[0].Revision','');",
                "revision-provenance" => "UPDATE editor_captures SET revision=revision+1;",
                _ => "DELETE FROM editor_capture_history WHERE generation=(SELECT MAX(generation) FROM editor_capture_history);",
            };
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        else fixture.Defect = damage;
        var before = await fixture.SnapshotAsync();
        var proof = await LocalCaptureProof(fixture);
        var requests = fixture.Native.Http.Requests;
        var exact = await editor.ReadLocalRelationshipReviewAsync(created.CaptureId, created.Proposal.Digest);
        exact.IsSuccess.ShouldBeFalse("a cached successful operation cannot certify a changed persisted original");
        var all = await editor.ReadLocalRelationshipReviewsAsync();
        all.IsSuccess.ShouldBeFalse();
        all.Error.Code.ShouldBe(exact.Error.Code);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Writes.ShouldBe(0);
    }

    private static async Task AssertExactLocalRead(HumanEditorFixture fixture, NativeItemEditor editor, NativeLocalRelationshipReview expected)
    {
        var before = await fixture.SnapshotAsync();
        var proof = await LocalCaptureProof(fixture);
        var requests = fixture.Native.Http.Requests;
        var credentials = fixture.Native.CredentialCalls;
        var writes = fixture.Writes;
        var exact = await editor.ReadLocalRelationshipReviewAsync(expected.CaptureId, expected.Proposal.Digest);
        exact.IsSuccess.ShouldBeTrue(exact.IsSuccess ? "" : exact.Error.Message);
        exact.Value.CaptureId.ShouldBe(expected.CaptureId);
        Model.ProposalBytes(exact.Value.Proposal).ShouldBe(Model.ProposalBytes(expected.Proposal));
        AssertLocalOutcome(expected.Outcome.ShouldNotBeNull(), exact.Value.Outcome.ShouldNotBeNull());
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await LocalCaptureProof(fixture)).ShouldBe(proof);
        fixture.Native.Http.Requests.ShouldBe(requests);
        fixture.Native.CredentialCalls.ShouldBe(credentials);
        fixture.Writes.ShouldBe(writes);
    }

    private static void AssertLocalOutcome(NativeLocalRelationshipOutcome expected, NativeLocalRelationshipOutcome actual)
    {
        actual.CaptureId.ShouldBe(expected.CaptureId);
        actual.Digest.ShouldBe(expected.Digest);
        actual.Settled.ShouldBe(expected.Settled);
        if (expected.Journal is null) actual.Journal.ShouldBeNull();
        else
        {
            var row = actual.Journal.ShouldNotBeNull();
            (row.Digest, row.Operation, row.Attempt, row.Status)
                .ShouldBe((expected.Journal.Digest, expected.Journal.Operation, expected.Journal.Attempt, expected.Journal.Status));
            row.Receipts.Select(ReceiptBytes).ShouldBe(expected.Journal.Receipts.Select(ReceiptBytes));
        }
        (actual.Error?.Code, actual.Error?.Message).ShouldBe((expected.Error?.Code, expected.Error?.Message));
        actual.Error?.Evidence.ShouldBe(expected.Error?.Evidence);
    }

    private static async Task<string> LocalCaptureProof(HumanEditorFixture fixture)
    {
        var proof = new StringBuilder();
        foreach (var (path, queries) in new[]
        {
            (Path.Combine(Path.GetDirectoryName(fixture.Host.Registry)!, "benches.db"), new[]
            {
                "SELECT instance,editor_registry,editor_generation,editor_head,editor_initialized FROM meta;",
                "SELECT * FROM editor_capture_meta ORDER BY id;",
                "SELECT * FROM editor_captures ORDER BY id;",
                "SELECT * FROM editor_capture_history ORDER BY generation;",
            }),
            (fixture.Host.Registry, new[]
            {
                "SELECT * FROM editor_provenance_meta ORDER BY id;",
                "SELECT * FROM editor_user_heads ORDER BY path;",
                "SELECT * FROM editor_capture_refs ORDER BY user_id,generation;",
            }),
        })
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync();
            foreach (var query in queries)
            {
                proof.Append(query);
                using var command = db.CreateCommand();
                command.CommandText = query;
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        var value = reader.GetValue(index).ToString()!;
                        proof.Append(value.Length).Append(':').Append(value);
                    }
            }
        }
        return proof.ToString();
    }
}
