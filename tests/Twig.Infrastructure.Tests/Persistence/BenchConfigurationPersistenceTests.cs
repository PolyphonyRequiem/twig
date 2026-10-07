using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class BenchConfigurationPersistenceTests
{
    [Fact]
    public async Task GuardedDelete_SelectorReorderingPreservesReviewedContents()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var bench = (await repository.CreateAsync("reordered"))!;
        var item = BenchSelector.ForItem(42);
        var query = BenchSelector.ForCurrentSprint("Saved owner");
        await repository.AddSelectorAsync(bench.Id, item);
        await repository.AddSelectorAsync(bench.Id, query);
        var digest = BenchQueryRule.ContentsDigest((await repository.GetByNameAsync(bench.Name))!.Selectors);
        await repository.RemoveSelectorAsync(bench.Id, item);
        await repository.AddSelectorAsync(bench.Id, item);

        (await repository.TryDeleteAsync(bench.Id, digest)).ShouldNotBeNull();
        (await repository.GetByNameAsync(bench.Name)).ShouldBeNull();
    }

    [Fact]
    public async Task GuardedDelete_RejectsPinKindAndRawAutomaticChangesEvenWhenCountsMatch()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var bench = (await repository.CreateAsync("reviewed"))!;
        var item = BenchSelector.ForItem(42);
        var tree = BenchSelector.ForSubtree(42);
        var firstQuery = BenchSelector.ForCurrentSprint("First owner");
        var changedQuery = BenchSelector.ForCurrentSprint("Different owner");
        await repository.AddSelectorAsync(bench.Id, item);
        await repository.AddSelectorAsync(bench.Id, firstQuery);
        var digest = BenchQueryRule.ContentsDigest((await repository.GetByNameAsync(bench.Name))!.Selectors);

        await repository.RemoveSelectorAsync(bench.Id, item);
        await repository.AddSelectorAsync(bench.Id, tree);
        (await repository.TryDeleteAsync(bench.Id, digest)).ShouldBeNull();
        (await repository.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([tree, firstQuery], ignoreOrder: true);

        await repository.RemoveSelectorAsync(bench.Id, tree);
        await repository.AddSelectorAsync(bench.Id, item);
        await repository.RemoveSelectorAsync(bench.Id, firstQuery);
        await repository.AddSelectorAsync(bench.Id, changedQuery);
        (await repository.TryDeleteAsync(bench.Id, digest)).ShouldBeNull();
        var stored = (await repository.GetByNameAsync(bench.Name))!;
        stored.Selectors.ShouldBe([item, changedQuery], ignoreOrder: true);
        (await repository.TryDeleteAsync(bench.Id, BenchQueryRule.ContentsDigest(stored.Selectors))).ShouldNotBeNull();
        (await repository.GetByNameAsync(bench.Name)).ShouldBeNull();
    }

    [Fact]
    public async Task GuardedLifecycle_RejectsRecreatedTargetAndProtectsDefaultInStorage()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var defaultBench = await repository.GetOrCreateDefaultAsync([BenchSelector.ForItem(99)]);
        var original = (await repository.CreateAsync("reviewed"))!;
        var emptyDigest = BenchQueryRule.ContentsDigest(original.Selectors);
        await repository.DeleteAsync(original.Id);
        var replacement = (await repository.CreateAsync(original.Name))!;

        replacement.Id.ShouldNotBe(original.Id);
        (await repository.TrySetCurrentAsync(original.Id)).ShouldBeNull();
        (await repository.TryDeleteAsync(original.Id, emptyDigest)).ShouldBeNull();
        (await repository.GetByNameAsync(replacement.Name))!.Id.ShouldBe(replacement.Id);
        (await repository.GetCurrentAsync()).ShouldBeNull();
        (await repository.TryDeleteAsync(defaultBench.Id, BenchQueryRule.ContentsDigest(defaultBench.Selectors))).ShouldBeNull();
        (await repository.GetByNameAsync(Bench.DefaultName))!.Selectors.ShouldBe([BenchSelector.ForItem(99)]);
    }

    [Fact]
    public async Task GuardedDelete_FailedCommitRollsBackSelectorsAndCurrentPointer()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var bench = (await repository.CreateAsync("rollback"))!;
        await repository.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        await repository.SetCurrentAsync(bench.Id);
        var stored = (await repository.GetByNameAsync(bench.Name))!;
        using (var trigger = store.GetConnection().CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER pending.refuse_bench_delete BEFORE DELETE ON benches
                BEGIN SELECT RAISE(ABORT, 'Simulated deletion failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Should.ThrowAsync<SqliteException>(() => repository.TryDeleteAsync(bench.Id, BenchQueryRule.ContentsDigest(stored.Selectors)));

        (await repository.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([BenchSelector.ForItem(42)]);
        (await repository.GetCurrentAsync())!.Id.ShouldBe(bench.Id);
        (await repository.TrySetCurrentAsync(bench.Id))!.Id.ShouldBe(bench.Id);
    }

    [Fact]
    public async Task ReplacingQueries_PreservesPinsOtherBenchesAndSettingsAfterReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-bench-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "twig.db")}";
        var replacement = new BenchQueryRule("Saved owner", null, [new("Project\\Team", false)],
            [BenchQueryRule.ParseSprint("@Current+1"), BenchQueryRule.ParseSprint("Project\\Release")]).ToSelector();
        try
        {
            using (var store = new SqliteCacheStore(connection))
            {
                var repository = new SqliteBenchRepository(store);
                var bench = (await repository.CreateAsync("configured"))!;
                var other = (await repository.CreateAsync("untouched"))!;
                await repository.SetCurrentAsync(bench.Id);
                await repository.AddSelectorAsync(bench.Id, BenchSelector.ForCurrentSprint("Saved owner"));
                await repository.AddSelectorAsync(bench.Id, BenchSelector.ForItem(99));
                await repository.AddSelectorAsync(bench.Id, BenchSelector.ForSubtree(101));
                await repository.AddSelectorAsync(other.Id, BenchSelector.ForCurrentSprint(null));
                var stored = (await repository.GetByNameAsync(bench.Name))!;
                (await repository.TryReplaceQuerySelectorsAsync(bench.Id, BenchQueryRule.SettingsDigest(stored.Selectors), [replacement])).ShouldBeTrue();
                (await repository.GetByNameAsync(other.Name))!.Selectors.ShouldBe([BenchSelector.ForCurrentSprint(null)]);
            }
            using (var reopened = new SqliteCacheStore(connection))
            {
                var stored = (await new SqliteBenchRepository(reopened).GetByNameAsync("configured"))!;
                stored.Selectors.ShouldBe([replacement, BenchSelector.ForItem(99), BenchSelector.ForSubtree(101)], ignoreOrder: true);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(TrackingMode.Single)]
    [InlineData(TrackingMode.Tree)]
    public async Task TypedPinRemoval_PersistsOnlyRequestedDeletionAndPreservesOtherWork(TrackingMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-pin-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "twig.db")}";
        var query = BenchSelector.ForCurrentSprint("Saved owner");
        var retained = mode == TrackingMode.Single ? BenchSelector.ForSubtree(42) : BenchSelector.ForItem(42);
        try
        {
            using (var store = new SqliteCacheStore(connection))
            {
                var repository = new SqliteBenchRepository(store);
                var bench = (await repository.CreateAsync("edited"))!;
                var other = (await repository.CreateAsync("untouched"))!;
                await repository.SetCurrentAsync(bench.Id);
                foreach (var selector in new[] { query, BenchSelector.ForItem(42), BenchSelector.ForSubtree(42), BenchSelector.ForSubtree(500) })
                    await repository.AddSelectorAsync(bench.Id, selector);
                await repository.AddSelectorAsync(other.Id, BenchSelector.ForItem(42));
                await repository.AddSelectorAsync(other.Id, BenchSelector.ForSubtree(42));
                var items = new SqliteWorkItemRepository(store, new WorkItemMapper());
                await items.SaveAsync(new WorkItemBuilder(42, "Owed item").Build());
                await items.SaveAsync(new WorkItemBuilder(-1, "Seed").AsSeed().Build());
                await new SqlitePendingChangeStore(store).AddChangeAsync(42, "note", null, null, "Preserve this note");
                using (var trigger = store.GetConnection().CreateCommand())
                {
                    trigger.CommandText = """
                        CREATE TRIGGER pending.refuse_retained_pin_delete BEFORE DELETE ON bench_selectors
                        WHEN OLD.selector_kind = @retainedKind
                        BEGIN SELECT RAISE(ABORT, 'Must not remove the other pin kind'); END;
                        """.Replace("@retainedKind", "'" + retained.Kind + "'", StringComparison.Ordinal);
                    await trigger.ExecuteNonQueryAsync();
                    trigger.CommandText = """
                        CREATE TRIGGER pending.refuse_pin_reinsert BEFORE INSERT ON bench_selectors
                        BEGIN SELECT RAISE(ABORT, 'Must not remove and re-add pins'); END;
                        """;
                    await trigger.ExecuteNonQueryAsync();
                }

                var digest = BenchQueryRule.SettingsDigest((await repository.GetByNameAsync(bench.Name))!.Selectors);
                var removal = await repository.TryRemovePinsAsync(bench.Id, 42, mode, digest);
                removal.ShouldNotBeNull();
                removal.Value.WasPinned.ShouldBeTrue();
                removal.Value.Bench.Id.ShouldBe(bench.Id);
                removal.Value.Bench.Selectors.ShouldBe([query, retained, BenchSelector.ForSubtree(500)], ignoreOrder: true);
                (await repository.TryRemovePinsAsync(bench.Id, 42, mode, digest))!.Value.WasPinned.ShouldBeFalse();
            }
            using (var reopened = new SqliteCacheStore(connection))
            {
                var repository = new SqliteBenchRepository(reopened);
                (await repository.GetByNameAsync("edited"))!.Selectors.ShouldBe([query, retained, BenchSelector.ForSubtree(500)], ignoreOrder: true);
                (await repository.GetByNameAsync("untouched"))!.Selectors.ShouldBe([BenchSelector.ForItem(42), BenchSelector.ForSubtree(42)], ignoreOrder: true);
                var changes = await new SqlitePendingChangeStore(reopened).GetChangesAsync(42);
                changes.ShouldHaveSingleItem().NewValue.ShouldBe("Preserve this note");
                (await new SqliteWorkItemRepository(reopened, new WorkItemMapper()).GetSeedsAsync()).Select(item => item.Id).ShouldBe([-1]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PinRemoval_FailedDeleteRollsBackAndReleasesTransaction()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var bench = (await repository.CreateAsync("rollback"))!;
        await repository.SetCurrentAsync(bench.Id);
        await repository.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        await repository.AddSelectorAsync(bench.Id, BenchSelector.ForSubtree(42));
        using (var trigger = store.GetConnection().CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER pending.refuse_subtree_delete BEFORE DELETE ON bench_selectors
                WHEN OLD.selector_kind = 'Subtree'
                BEGIN SELECT RAISE(ABORT, 'Simulated delete failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Should.ThrowAsync<SqliteException>(() => repository.TryRemovePinsAsync(bench.Id, 42));

        (await repository.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([BenchSelector.ForItem(42), BenchSelector.ForSubtree(42)], ignoreOrder: true);
        (await repository.TryRemovePinsAsync(bench.Id, 42, TrackingMode.Single))!.Value.WasPinned.ShouldBeTrue();
        (await repository.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([BenchSelector.ForSubtree(42)]);
    }

    [Fact]
    public async Task AuthoredAbsoluteSprints_MatchDifferentCacheCasingAfterReopenWithoutWideningMembership()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-bench-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "twig.db")}";
        var rule = new BenchQueryRule(null, "owner@example.test", [new("Project\\Team", false)],
            [BenchQueryRule.ParseSprint("project\\sprint 7"), BenchQueryRule.ParseSprint("pröject\\sprint 7")]);
        try
        {
            using (var store = new SqliteCacheStore(connection))
            {
                var benches = new SqliteBenchRepository(store);
                var bench = (await benches.CreateAsync("authored"))!;
                await benches.SetCurrentAsync(bench.Id);
                (await benches.TryReplaceQuerySelectorsAsync(bench.Id,
                    BenchQueryRule.SettingsDigest(bench.Selectors), [rule.ToSelector()])).ShouldBeTrue();
                var items = new SqliteWorkItemRepository(store, new WorkItemMapper());
                await items.SaveBatchAsync([
                    CachedItem(1, "Project\\Sprint 7"),
                    CachedItem(2, "PrÖject\\Sprint 7"),
                    CachedItem(3, "Project\\Sprint 7\\Child"),
                    CachedItem(4, "Project\\Sprint 70"),
                    CachedItem(5, "Project\\Sprint 7", area: "Project\\Team\\Child"),
                    CachedItem(6, "Project\\Sprint 7", owner: "other@example.test"),
                    CachedItem(7, "Próject\\Sprint 7"),
                ]);
            }
            using (var reopened = new SqliteCacheStore(connection))
            {
                var bench = (await new SqliteBenchRepository(reopened).GetByNameAsync("authored"))!;
                var items = new SqliteWorkItemRepository(reopened, new WorkItemMapper());
                var membership = await new BenchEvaluator(items, new SqliteIterationCalendar(reopened),
                    new SqlitePendingChangeStore(reopened)).EvaluateAsync(bench);
                membership.QueryMatches.Select(item => item.Id).ShouldBe([1, 2], ignoreOrder: true);
                membership.AllIds.ShouldBe([1, 2], ignoreOrder: true);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }

        static WorkItem CachedItem(int id, string iteration, string area = "Project\\Team", string owner = "owner@example.test")
            => new WorkItemBuilder(id, "Cached " + id).WithIterationPath(iteration).WithAreaPath(area)
                .AssignedToUniqueName(owner).Build();
    }

    [Fact]
    public async Task StaleRawSettingsOrCurrentBench_RefusesQueryAndPinChanges()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var original = (await repository.CreateAsync("original"))!;
        var other = (await repository.CreateAsync("other"))!;
        await repository.SetCurrentAsync(original.Id);
        var digest = BenchQueryRule.SettingsDigest(original.Selectors);
        await repository.AddSelectorAsync(original.Id, BenchSelector.ForItem(88));
        await repository.AddSelectorAsync(original.Id, BenchSelector.ForSubtree(88));
        await repository.AddSelectorAsync(original.Id, BenchSelector.ForCurrentSprint("Changed owner"));
        (await repository.TryReplaceQuerySelectorsAsync(original.Id, digest, [])).ShouldBeFalse();
        (await repository.TryUpdatePinsAsync(original.Id, 88, false, false, digest)).ShouldBeFalse();
        (await repository.TryRemovePinsAsync(original.Id, 88, TrackingMode.Single, digest)).ShouldBeNull();
        (await repository.TryRemovePinsAsync(original.Id, 88, TrackingMode.Tree, digest)).ShouldBeNull();
        await repository.SetCurrentAsync(other.Id);
        digest = BenchQueryRule.SettingsDigest((await repository.GetByNameAsync(original.Name))!.Selectors);
        (await repository.TryReplaceQuerySelectorsAsync(original.Id, digest, [])).ShouldBeFalse();
        (await repository.TryUpdatePinsAsync(original.Id, 88, false, false, digest)).ShouldBeFalse();
        (await repository.TryRemovePinsAsync(original.Id, 88, TrackingMode.Single, digest)).ShouldBeNull();
        (await repository.TryRemovePinsAsync(original.Id, 88, TrackingMode.Tree, digest)).ShouldBeNull();
        (await repository.GetByNameAsync(original.Name))!.Selectors.ShouldBe([
            BenchSelector.ForItem(88), BenchSelector.ForSubtree(88), BenchSelector.ForCurrentSprint("Changed owner")], ignoreOrder: true);
        (await repository.GetByNameAsync(other.Name))!.Selectors.ShouldBeEmpty();
    }

    [Fact]
    public async Task DefaultCanonicalNormalization_PreservesAuthoredFiltersAndRawDigest()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = new SqliteBenchRepository(store);
        var authored = new BenchQueryRule(null, "old@example.test", [new("Project\\Team", true)],
            [BenchQueryRule.ParseSprint("@Current-1"), BenchQueryRule.ParseSprint("Project\\Release")]);
        var stored = await repository.GetOrCreateDefaultAsync([authored.ToSelector(), BenchSelector.ForItem(88)]);
        var rawDigest = BenchQueryRule.SettingsDigest(stored.Selectors);
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)"Same display", (string?)"new@example.test"));
        var resolver = new CurrentBenchResolver(repository, new DefaultBenchSelectors(iterations));
        var (effective, raw) = await resolver.ResolveCapturedAsync();
        var normalized = BenchQueryRule.Parse(effective.Selectors.Single(s => s.Kind == Domain.Enums.SelectorKind.Query));
        normalized.UniqueName.ShouldBe("new@example.test");
        normalized.Areas.ShouldBe(authored.Areas);
        normalized.Sprints.ShouldBe(authored.Sprints);
        effective.Selectors.ShouldContain(BenchSelector.ForItem(88));
        BenchQueryRule.SettingsDigest(raw.Selectors).ShouldBe(rawDigest);
        var cleared = normalized with { Sprints = [] };
        (await repository.TryReplaceQuerySelectorsAsync(stored.Id, rawDigest, [cleared.ToSelector()])).ShouldBeTrue();
        var resolvedAgain = await resolver.ResolveAsync();
        BenchQueryRule.Parse(resolvedAgain.Selectors.Single(s => s.Kind == Domain.Enums.SelectorKind.Query)).Sprints.ShouldBeEmpty();
    }

    [Fact]
    public async Task CachedRelativeExpressions_RollWithClockAndNeverInventMissingIterations()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var calendar = new SqliteIterationCalendar(store, () => now);
        await calendar.SaveAsync([
            new("Project\\A", now.AddDays(-10), now.AddDays(-3)),
            new("Project\\B", now.AddDays(-2), now.AddDays(2)),
            new("Project\\C", now.AddDays(3), now.AddDays(10)),
            new("Project\\Undated", null, null),
        ]);
        var relative = BenchQueryRule.ParseSprint("@Current+1");
        (await calendar.ResolveExpressionAsync(relative)).Select(path => path.Value).ShouldBe(["Project\\C"]);
        now = now.AddDays(5);
        (await calendar.ResolveExpressionAsync(relative)).ShouldBeEmpty();
        (await calendar.ResolveExpressionAsync(BenchQueryRule.ParseSprint("@Current-1")))
            .Select(path => path.Value).ShouldBe(["Project\\B"]);
        (await calendar.ResolveExpressionAsync(BenchQueryRule.ParseSprint("Project\\Absolute")))
            .Select(path => path.Value).ShouldBe(["Project\\Absolute"]);
        now = now.AddDays(100);
        (await calendar.ResolveExpressionAsync(BenchQueryRule.ParseSprint("@Current-1"))).ShouldBeEmpty();
    }
}
