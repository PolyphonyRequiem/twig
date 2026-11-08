using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeGitHubLocalReviewUsesExactDigestAndVerifiedCasWithoutScalarPublication()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var earlierScalar = (await editor.SelectAsync(fixture.Key)).Value;
        var selected = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        selected.LocalOnly.ShouldBeTrue();
        selected.Verification.ShouldContain("no native tracker mutation");
        selected.Choices.ShouldBeEmpty();
        (await editor.EvaluateAsync(selected.Fields.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
        (await editor.SaveAsync(selected.Fields.Ticket, FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("do not adopt"))))
            .IsSuccess.ShouldBeFalse();
        (await editor.StageRelationshipsAsync(selected.Fields.Ticket, FieldValues.Empty,
            [new(LocalPairOther(fixture), selected.Relations.Single().Reference, true)])).IsSuccess.ShouldBeFalse();
        (await editor.SaveRelationshipsAsync(selected.Fields.Ticket, FieldValues.Empty,
            [new(LocalPairOther(fixture), selected.Relations.Single().Reference, true)])).IsSuccess.ShouldBeFalse();
        var reviewed = await editor.ReviewLocalRelationshipAsync(selected.Fields.Ticket, true);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        var review = reviewed.Value;
        review.Proposal.Operations.ShouldHaveSingleItem().Origin.ShouldBe(RelationshipOrigin.Local);
        review.Proposal.Operations.Single().Kind.ShouldBe(OpKind.Relate);
        review.Proposal.Operations.Single().Fields.ShouldBeEmpty();
        var before = await fixture.SnapshotAsync();
        before.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Planned);
        before.Drafts.ShouldBeEmpty();
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var captured = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        captured.LocalRelationship.ShouldNotBeNull();
        captured.DraftFingerprint.ShouldBe("none");
        captured.SeedProposal.ShouldBeNull();
        captured.Ready.ShouldBeTrue();
        captured.IntentJson.ShouldBe(Encoding.UTF8.GetString(Model.ProposalBytes(review.Proposal)));
        (await editor.EvaluateAsync(earlierScalar.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
        (await editor.SaveAsync(earlierScalar.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
        (await editor.SelectAsync(fixture.Key)).IsSuccess.ShouldBeFalse();
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        (await editor.ApplyLocalRelationshipAsync(review.CaptureId, new string('0', 64))).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        var result = await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Error.ShouldBeNull();
        result.Value.Journal.ShouldNotBeNull().Status.ShouldBe(OpStatus.Verified);
        result.Value.Settled.ShouldBeTrue();
        result.Value.Journal.Receipts.ShouldAllBe(receipt => receipt.PublishedKey == null && receipt.Revision == null);
        var after = await fixture.SnapshotAsync();
        after.Relationships.ShouldHaveSingleItem().LocalVersion.ShouldBe(1);
        after.Relationships.Single().Origin.ShouldBe(RelationshipOrigin.Local);
        after.Drafts.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
        var originalJournal = JournalBytes(after);
        (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        using var reopened = (await fixture.OpenAsync()).Value;
        var observed = await reopened.ObserveLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        observed.IsSuccess.ShouldBeTrue();
        observed.Value.Journal!.Status.ShouldBe(OpStatus.Verified);
        observed.Value.Settled.ShouldBeTrue();
        (await reopened.ContinueLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value.Settled.ShouldBeTrue();
        JournalBytes(await fixture.SnapshotAsync()).ShouldBe(originalJournal);
        var scalar = (await reopened.SelectAsync(fixture.Key)).Value;
        (await reopened.SaveAsync(scalar.Ticket, FieldValues.Empty.Set(GitHubProvider.Title, FieldValue.String("separate scalar"))))
            .Value.Verified.ShouldBeTrue();
        fixture.Writes.ShouldBe(1, "only the separately requested scalar Save may mutate GitHub");
        var historical = (await reopened.ObserveLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value;
        historical.Error.ShouldBeNull();
        historical.Journal!.Status.ShouldBe(OpStatus.Verified);
        historical.Journal.Receipts.Select(ReceiptBytes).ShouldBe(observed.Value.Journal!.Receipts.Select(ReceiptBytes));
    }

    [Fact]
    public async Task NativeGitHubLocalReverseOrderRetainsOriginalUuidAndTombstoneAbaCas()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        // Obtain original UUID publication evidence through the real CLI, not fabricated mappings.
        fixture.Native.Run("seed", "new", "--type", GitHubProvider.Untyped, "--title", "mapped source",
            "--no-parent", "-o", "json").ShouldBe(0, fixture.Native.Error);
        var seed = (await fixture.SnapshotAsync()).Drafts.Single();
        fixture.Native.Run("link", "local", "related", "43", "--id", seed.Key.LocalId.ToString("D"), "-o", "json")
            .ShouldBe(0, fixture.Native.Error);
        var original = (await fixture.SnapshotAsync()).Relationships.Single();
        original.LocalVersion.ShouldBe(1);
        (original.From == seed.Key || original.To == seed.Key).ShouldBeTrue();
        fixture.Native.Run("seed", "publish", CoreHostContext.LocalAlias(seed.Key).ToString(
            System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(0, fixture.Native.Error);
        var published = await fixture.SnapshotAsync();
        published.Mappings.ShouldHaveSingleItem().Local.ShouldBe(seed.Key);
        published.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
        var mapping = published.Mappings.Single();
        using (var editor = (await fixture.OpenAsync()).Value)
        {
            var pair = (await editor.SelectRelationshipsAsync(LocalPairOther(fixture), fixture.Key)).Value;
            var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, false)).Value;
            review.Proposal.Operations.Single().ExpectedLocalVersion.ShouldBe(1);
            (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value.Journal!.Status.ShouldBe(OpStatus.Verified);
        }
        (await fixture.SnapshotAsync()).Relationships.Single().ShouldBe(original with { LocalVersion = 2, Deleted = true });
        using (var editor = (await fixture.OpenAsync()).Value)
        {
            var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
            var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
            review.Proposal.Operations.Single().ExpectedLocalVersion.ShouldBe(2);
            (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value.Journal!.Status.ShouldBe(OpStatus.Verified);
        }
        var final = await fixture.SnapshotAsync();
        final.Relationships.Single().ShouldBe(original with { LocalVersion = 3 });
        final.Mappings.Single().ShouldBe(mapping);
        await fixture.Native.UseWorkspace(async workspace =>
        {
            var stale = await workspace.PrepareAsync([new("stale", OpKind.Unrelate, original.From, other: original.To,
                relation: original.Kind, origin: RelationshipOrigin.Local, expectedLocalVersion: 1)], default);
            stale.Error.Code.ShouldBe(ErrorCode.Conflict);
        });
        fixture.Writes.ShouldBe(1, "only the explicitly requested seed publication writes the tracker");
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("repository")]
    [InlineData("schema")]
    [InlineData("endpoint")]
    [InlineData("mode")]
    [InlineData("store")]
    [InlineData("draft")]
    [InlineData("policy")]
    [InlineData("denied")]
    [InlineData("bench")]
    [InlineData("edge")]
    public async Task NativeGitHubLocalOriginalReviewRefusesInvalidatedProofWithoutReplacement(string damage)
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var before = await fixture.SnapshotAsync();
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        if (damage == "endpoint") fixture.Native.Http.OtherIssue!["updatedAt"] = "2026-11-08T12:00:00Z";
        else if (damage == "mode") await fixture.SetPlanAsync(true);
        else if (damage == "store") await fixture.Native.ReplaceItems(before.Items);
        else if (damage == "edge")
            await fixture.Native.ReplaceItems(before.Items, [new(fixture.Key, LocalPairOther(fixture),
                review.Proposal.Operations.Single().Relation!, RelationshipOrigin.Local, 1)]);
        else if (damage == "bench")
        {
            var originalBench = (await user.ReadAsync()).Value.Benches.Single(bench => bench.Id == fixture.Host.Bench);
            (await user.SaveAsync(new(originalBench.Id, "changed", originalBench.Selectors), originalBench.Revision))
                .IsSuccess.ShouldBeTrue();
        }
        else if (damage == "draft")
            await fixture.Native.UseWorkspace(async workspace =>
            {
                (await workspace.StageEditAsync(LocalPairOther(fixture), FieldValues.Empty.Set(GitHubProvider.Title,
                    FieldValue.String("independent debt")), default)).IsSuccess.ShouldBeTrue();
            });
        else if (damage == "policy")
            await File.WriteAllTextAsync(Path.Combine(fixture.Host.Root, "twig.policy.json"), """{"version":1,"rules":[]}""");
        else fixture.Defect = damage == "denied" ? "schema-denied" : damage;
        var invalidated = await fixture.SnapshotAsync();
        var result = await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        (result.IsSuccess && result.Value.Journal?.Status == OpStatus.Verified).ShouldBeFalse();
        AssertSameLocalState(invalidated, await fixture.SnapshotAsync());
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(capture).ShouldBeTrue();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalCaptureWitnessCannotBecomeAbsentIntent()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var before = await fixture.SnapshotAsync();
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetDirectoryName(fixture.Host.Registry)!, "benches.db"), Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM editor_captures;";
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await editor.ObserveLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).IsSuccess.ShouldBeFalse();
        var after = await fixture.SnapshotAsync();
        AssertSameLocalState(before, after);
        after.Drafts.ShouldBeEmpty();
        after.Relationships.ShouldBeEmpty();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalQualificationRejectsImplicitScopeAndPreservesMissingRemove()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var selected = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        selected.LocalOnly.ShouldBeTrue();
        var before = await fixture.SnapshotAsync();
        (await editor.SelectRelationshipsAsync(fixture.Key, fixture.Key)).IsSuccess.ShouldBeFalse();
        (await editor.SelectRelationshipsAsync(fixture.Key, ItemKey.Local(Guid.CreateVersion7()))).IsSuccess.ShouldBeFalse();
        (await editor.SelectRelationshipsAsync(fixture.Key, ItemKey.Remote(fixture.Host.Stamp.Connection.Provider, "R_other", "I_other")))
            .IsSuccess.ShouldBeFalse();
        (await editor.ReviewLocalRelationshipAsync(selected.Fields.Ticket, false)).Error.Code.ShouldBe(ErrorCode.Conflict);
        AssertSameLocalState(before, await fixture.SnapshotAsync());
        await fixture.SetPlanAsync(true);
        using var plan = (await fixture.OpenAsync()).Value;
        plan.PlanMode.ShouldBeTrue();
        (await plan.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).IsSuccess.ShouldBeFalse();
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalAppliedReceiptReopensAndVerifiesWithoutReplay()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        SqliteConnection? blocker = null;
        SqliteTransaction? transaction = null;
        fixture.Native.Http.BeforeSchema = async () =>
        {
            var snapshot = await fixture.SnapshotAsync();
            if (blocker is not null || snapshot.Journal.Single().Status != OpStatus.Applied) return;
            snapshot.Relationships.Single().LocalVersion.ShouldBe(1);
            blocker = new(new SqliteConnectionStringBuilder
                { DataSource = fixture.Native.Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await blocker.OpenAsync();
            transaction = blocker.BeginTransaction(deferred: false);
        };
        try
        {
            var interrupted = await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
            interrupted.IsSuccess.ShouldBeTrue();
            interrupted.Value.Error.ShouldNotBeNull();
            interrupted.Value.Journal!.Status.ShouldBe(OpStatus.Applied, "a real SQLite writer fence must leave the original durable Applied receipt");
            interrupted.Value.Settled.ShouldBeFalse();
        }
        finally
        {
            fixture.Native.Http.BeforeSchema = null;
            transaction?.Dispose();
            blocker?.Dispose();
        }
        var original = await fixture.SnapshotAsync();
        original.Journal.Single().Status.ShouldBe(OpStatus.Applied);
        var receipt = original.Journal.Single().Receipts.Single();
        var edge = original.Relationships.Single();
        (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        AssertSameLocalState(original, await fixture.SnapshotAsync());
        using var reopened = (await fixture.OpenAsync()).Value;
        var continued = await reopened.ContinueLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        continued.IsSuccess.ShouldBeTrue(continued.IsSuccess ? "" : continued.Error.Message);
        continued.Value.Error.ShouldBeNull();
        continued.Value.Journal!.Status.ShouldBe(OpStatus.Verified);
        continued.Value.Journal.Attempt.ShouldBe(receipt.Attempt);
        continued.Value.Settled.ShouldBeTrue();
        var final = await fixture.SnapshotAsync();
        final.Relationships.Single().ShouldBe(edge);
        final.Proposals.Length.ShouldBe(1);
        final.Journal.Single().Receipts.Length.ShouldBe(2);
        ReceiptBytes(final.Journal.Single().Receipts[0]).ShouldBe(ReceiptBytes(receipt));
        fixture.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeGitHubLocalLateAcquisitionCannotReplaceCapturedEndpointProof()
    {
        using var fixture = new HumanEditorFixture();
        await InitializeLocalPair(fixture);
        using var editor = (await fixture.OpenAsync()).Value;
        var pair = (await editor.SelectRelationshipsAsync(fixture.Key, LocalPairOther(fixture))).Value;
        var review = (await editor.ReviewLocalRelationshipAsync(pair.Fields.Ticket, true)).Value;
        var user = UserBenchStore.ForRegistry(fixture.Host.Registry);
        var captured = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        var reads = 0;
        fixture.Native.Http.BeforeIssueRead = id =>
        {
            if (id == "I_other" && ++reads == 3)
                fixture.Native.Http.OtherIssue!["updatedAt"] = "2026-11-08T16:00:00Z";
            return Task.CompletedTask;
        };
        var result = await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        fixture.Native.Http.BeforeIssueRead = null;
        reads.ShouldBe(4, "each coherent read uses two requests; change on the provider's first request, after the facade's completed read");
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : $"{result.Error.Code}: {result.Error.Message}");
        result.Value.Error.ShouldNotBeNull();
        result.Value.Journal!.Status.ShouldBe(OpStatus.Applying);
        result.Value.Journal.Attempt.ShouldNotBe(Guid.Empty);
        result.Value.Journal.Receipts.ShouldBeEmpty();
        var original = await fixture.SnapshotAsync();
        original.Relationships.ShouldBeEmpty();
        original.Proposals.Single().Digest.ShouldBe(review.Proposal.Digest);
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(captured).ShouldBeTrue();
        (await editor.ApplyLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).IsSuccess.ShouldBeFalse();
        (await editor.ContinueLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest)).Value.Journal!.Status.ShouldBe(OpStatus.Applying);
        using var reopened = (await fixture.OpenAsync()).Value;
        var observed = await reopened.ObserveLocalRelationshipAsync(review.CaptureId, review.Proposal.Digest);
        observed.IsSuccess.ShouldBeTrue();
        observed.Value.Journal!.Status.ShouldBe(OpStatus.Applying);
        observed.Value.Error.ShouldNotBeNull();
        AssertSameLocalState(original, await fixture.SnapshotAsync());
        fixture.Writes.ShouldBe(0);
    }

    private static void AssertSameLocalState(StoreSnapshot expected, StoreSnapshot actual)
    {
        actual.Version.ShouldBe(expected.Version);
        actual.Items.ShouldBe(expected.Items);
        actual.Relationships.ShouldBe(expected.Relationships);
        actual.Drafts.ShouldBe(expected.Drafts);
        actual.Mappings.ShouldBe(expected.Mappings);
        actual.Proposals.Select(proposal => Encoding.UTF8.GetString(Model.ProposalBytes(proposal)))
            .ShouldBe(expected.Proposals.Select(proposal => Encoding.UTF8.GetString(Model.ProposalBytes(proposal))));
        JournalBytes(actual).ShouldBe(JournalBytes(expected));
    }

    private static string JournalBytes(StoreSnapshot state)
        => string.Join("\n", state.Journal.Select(row => $"{row.Digest}:{row.Operation}:{row.Attempt:D}:{row.Status}:"
            + string.Join("|", row.Receipts.Select(ReceiptBytes))));

    private static string ReceiptBytes(Receipt receipt)
        => $"{receipt.Digest}:{receipt.Operation}:{receipt.Attempt:D}:{receipt.Status}:{receipt.Revision}:{receipt.PublishedKey}:"
            + string.Join("|", receipt.Evidence) + $":{receipt.Error?.Code}:{receipt.Error?.Message}:"
            + string.Join("|", receipt.Error?.Evidence ?? []);
}
