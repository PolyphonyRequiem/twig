using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteRecoverableWorkItemDraftStoreTests
{
    [Fact]
    public async Task DraftSurvivesStoreReopen_AndCompareAndSwapProtectsSharedGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "twig.db");
        var key = Key("binding-a");
        var otherBinding = Key("binding-b");

        try
        {
            RecoverableWorkItemDraft first;
            using (var cache = new SqliteCacheStore($"Data Source={databasePath}"))
            {
                IRecoverableWorkItemDraftStore drafts = new SqliteRecoverableWorkItemDraftStore(cache);
                first = RecoverableWorkItemDraft.Create(key, baselineRevision: 5)
                    .SetField("System.Title", "baseline", "companion intent");
                (await drafts.TrySaveAsync(first, expectedGeneration: null)).ShouldBeTrue();
                (await drafts.TrySaveAsync(RecoverableWorkItemDraft.Create(otherBinding, 5), expectedGeneration: null))
                    .ShouldBeTrue();
            }

            using (var reopened = new SqliteCacheStore($"Data Source={databasePath}"))
            {
                IRecoverableWorkItemDraftStore drafts = new SqliteRecoverableWorkItemDraftStore(reopened);
                var loaded = await drafts.GetAsync(key);
                loaded.ShouldNotBeNull();
                loaded!.Generation.ShouldBe(first.Generation);
                loaded.BaselineRevision.ShouldBe(5);
                loaded.Changes.ShouldContain(new DraftFieldChange("System.Title", "baseline", "companion intent"));

                var workbenchEdit = loaded.SetField("System.State", "New", "Active");
                (await drafts.TrySaveAsync(workbenchEdit, expectedGeneration: loaded.Generation)).ShouldBeTrue();

                var staleCompanionEdit = loaded.SetField("System.Title", "baseline", "stale companion intent");
                (await drafts.TrySaveAsync(staleCompanionEdit, expectedGeneration: loaded.Generation)).ShouldBeFalse();

                var readBack = await drafts.GetAsync(key);
                readBack.ShouldNotBeNull();
                readBack!.Generation.ShouldBe(workbenchEdit.Generation);
                readBack.Changes.ShouldContain(new DraftFieldChange("System.Title", "baseline", "companion intent"));
                readBack.Changes.ShouldContain(new DraftFieldChange("System.State", "New", "Active"));
                (await drafts.GetAsync(otherBinding))!.Changes.ShouldBeEmpty();
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
