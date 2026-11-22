using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    private sealed class LocalHttpFixture : IDisposable
    {
        private SqliteConnection? blocker;
        private SqliteTransaction? transaction;
        private string? appliedReceipt;
        private string? appliedJournal;
        internal HumanEditorFixture Human { get; } = new();
        internal ItemKey Other => LocalPairOther(Human);

        internal async Task InitializeAsync()
        {
            await InitializeLocalPair(Human);
            var user = UserBenchStore.ForRegistry(Human.Host.Registry);
            var bench = (await user.ReadAsync()).Value.Benches.Single(value => value.Id == Human.Host.Bench);
            (await user.SaveAsync(new(bench.Id, bench.Name,
                [new(SelectorKind.Pin, Human.Key, connection: Human.Host.Stamp.Connection),
                    new(SelectorKind.Pin, Other, connection: Human.Host.Stamp.Connection)]),
                bench.Revision)).IsSuccess.ShouldBeTrue();
            await AssertStateAsync(0);
        }

        internal async Task AssertStateAsync(int effects)
        {
            var state = await Human.SnapshotAsync();
            state.Items.Length.ShouldBe(2);
            state.Items.ShouldAllBe(item => !item.Key.IsLocal && item.RelationshipsComplete && item.Revision != null);
            state.RetainedItems.ShouldBeEmpty();
            Human.Writes.ShouldBe(0);
            if (effects == 0)
            {
                state.Relationships.ShouldBeEmpty();
                state.Plans.ShouldBeEmpty();
                state.Journal.ShouldBeEmpty();
                (await UserBenchStore.ForRegistry(Human.Host.Registry).ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
                return;
            }
            state.Relationships.ShouldHaveSingleItem().LocalVersion.ShouldBe(effects);
            state.Relationships.Single().Origin.ShouldBe(RelationshipOrigin.Local);
            state.Relationships.Single().Deleted.ShouldBe(effects % 2 == 0);
            state.Plans.Length.ShouldBe(effects);
            state.Journal.Length.ShouldBe(effects);
            state.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified && row.Operation == 0 && row.Attempt != Guid.Empty);
            foreach (var row in state.Journal)
                row.Receipts.ShouldAllBe(receipt => receipt.PublishedKey == null && receipt.Revision == null);
            state.Items.Single(item => item.Key == Human.Key).Fields["github.title"].Text.ShouldBe("original");
        }

        internal async Task<Func<Task>> PreserveAsync()
        {
            var state = await Human.SnapshotAsync();
            var captures = (await UserBenchStore.ForRegistry(Human.Host.Registry).ReadEditorCapturesAsync(default)).Value;
            var proof = captures.IsEmpty ? null : await LocalCaptureProof(Human);
            return async () =>
            {
                AssertSameLocalState(state, await Human.SnapshotAsync());
                if (proof is not null) (await LocalCaptureProof(Human)).ShouldBe(proof);
                var retained = (await UserBenchStore.ForRegistry(Human.Host.Registry).ReadEditorCapturesAsync(default)).Value;
                retained.Length.ShouldBe(captures.Length);
                foreach (var capture in captures)
                    retained.Single(value => value.Id == capture.Id).SameAs(capture).ShouldBeTrue();
                Human.Writes.ShouldBe(0);
            };
        }

        private void ChangeEndpoint()
            => Human.Native.Http.OtherIssue!["updatedAt"] = "2026-11-08T16:00:00Z";

        internal void ArmUnknown()
        {
            var reads = 0;
            Human.Native.Http.BeforeIssueRead = id =>
            {
                if (id == "I_other" && ++reads == 3) ChangeEndpoint();
                return Task.CompletedTask;
            };
        }

        internal void ArmApplied()
        {
            Human.Native.Http.BeforeSchema = async () =>
            {
                var state = await Human.SnapshotAsync();
                if (blocker is not null || state.Journal.Last().Status != OpStatus.Applied) return;
                appliedReceipt = ReceiptBytes(state.Journal.Last().Receipts.Single());
                var row = state.Journal.Last();
                appliedJournal = $"{row.Digest}:{row.Operation}:{row.Attempt:D}";
                blocker = new(new SqliteConnectionStringBuilder
                    { DataSource = Human.Native.Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await blocker.OpenAsync();
                transaction = blocker.BeginTransaction(deferred: false);
            };
        }

        internal void ReleaseApplied()
        {
            Human.Native.Http.BeforeSchema = null;
            transaction?.Dispose(); transaction = null;
            blocker?.Dispose(); blocker = null;
        }

        internal async Task AssertContinuedAsync()
        {
            await AssertStateAsync(1);
            var row = (await Human.SnapshotAsync()).Journal.Single();
            row.Receipts.Length.ShouldBe(2);
            ReceiptBytes(row.Receipts[0]).ShouldBe(appliedReceipt);
            $"{row.Digest}:{row.Operation}:{row.Attempt:D}".ShouldBe(appliedJournal);
        }

        internal async Task DamageAsync(string damage)
        {
            if (damage == "endpoint") ChangeEndpoint();
            else if (damage == "mode") await Human.SetPlanAsync(true);
            else if (damage == "store") await Human.Native.ReplaceItems((await Human.SnapshotAsync()).Items);
            else if (damage == "bench")
            {
                var user = UserBenchStore.ForRegistry(Human.Host.Registry);
                var bench = (await user.ReadAsync()).Value.Benches.Single(value => value.Id == Human.Host.Bench);
                (await user.SaveAsync(new(bench.Id, "changed", bench.Selectors), bench.Revision)).IsSuccess.ShouldBeTrue();
            }
            else Human.Defect = damage == "denied" ? "schema-denied" : damage;
        }

        internal async Task AssertCanonicalAsync(Guid capture, string canonical, string digest)
        {
            var stored = (await UserBenchStore.ForRegistry(Human.Host.Registry).ReadEditorCapturesAsync(default)).Value
                .Single(value => value.Id == capture);
            stored.IntentJson.ShouldBe(canonical);
            var original = (await Human.SnapshotAsync()).Plans.Single(plan => plan.Digest == digest);
            Encoding.UTF8.GetString(Model.PlanBytes(original)).ShouldBe(canonical);
            original.Digest.ShouldBe(digest);
        }

        internal async Task AssertProtectedAsync()
        {
            var state = await Human.SnapshotAsync();
            state.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Applying);
            state.Journal.Single().Attempt.ShouldNotBe(Guid.Empty);
            state.Journal.Single().Receipts.ShouldBeEmpty();
            state.Relationships.ShouldBeEmpty();
            state.RetainedItems.ShouldBeEmpty();
            Human.Writes.ShouldBe(0);
        }

        public void Dispose()
        {
            Human.Native.Http.BeforeIssueRead = null;
            ReleaseApplied();
            Human.Dispose();
        }
    }
}
