using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

/// <summary>
/// Unit tests for <see cref="WorkItemCopier.Copy"/> — the centralized copy method.
/// </summary>
public sealed class WorkItemCopierCopyTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Default copy (no overrides) preserves all properties
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_NoOverrides_PreservesAllProperties()
    {
        var source = BuildFullSource();

        var copy = WorkItemCopier.Copy(source);

        copy.Id.ShouldBe(source.Id);
        copy.Type.ShouldBe(source.Type);
        copy.Title.ShouldBe(source.Title);
        copy.State.ShouldBe(source.State);
        copy.AssignedTo.ShouldBe(source.AssignedTo);
        copy.IterationPath.ShouldBe(source.IterationPath);
        copy.AreaPath.ShouldBe(source.AreaPath);
        copy.ParentId.ShouldBe(source.ParentId);
        copy.IsSeed.ShouldBe(source.IsSeed);
        copy.SeedCreatedAt.ShouldBe(source.SeedCreatedAt);
        copy.LastSyncedAt.ShouldBe(source.LastSyncedAt);
        copy.Revision.ShouldBe(source.Revision);
        copy.Fields.Count.ShouldBe(source.Fields.Count);
        copy.Fields["System.Description"].ShouldBe("desc");
    }

    [Fact]
    public void Copy_NoOverrides_ProducesCleanCopy()
    {
        var source = BuildFullSource();
        source.UpdateField("System.Title", "dirty");
        source.IsDirty.ShouldBeTrue();

        var copy = WorkItemCopier.Copy(source);

        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Copy_NoOverrides_DoesNotCopyPendingNotes()
    {
        var source = BuildFullSource();
        source.AddNote(new PendingNote("note", DateTimeOffset.UtcNow, false));
        source.PendingNotes.Count.ShouldBe(1);

        var copy = WorkItemCopier.Copy(source);

        copy.PendingNotes.Count.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  titleOverride
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_TitleOverride_ReplacesTitle()
    {
        var source = BuildFullSource();

        var copy = WorkItemCopier.Copy(source, titleOverride: "New Title");

        copy.Title.ShouldBe("New Title");
        copy.Id.ShouldBe(source.Id);
    }

    [Fact]
    public void Copy_TitleOverrideNull_PreservesSourceTitle()
    {
        var source = BuildFullSource();

        var copy = WorkItemCopier.Copy(source, titleOverride: null);

        copy.Title.ShouldBe(source.Title);
    }

    // ═══════════════════════════════════════════════════════════════
    //  overrideParentId / parentIdValue
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_OverrideParentId_SetsNewParent()
    {
        var source = BuildFullSource();

        var copy = WorkItemCopier.Copy(source, overrideParentId: true, parentIdValue: 999);

        copy.ParentId.ShouldBe(999);
    }

    [Fact]
    public void Copy_OverrideParentIdToNull_ClearsParent()
    {
        var source = BuildFullSource();
        source.ParentId.ShouldNotBeNull();

        var copy = WorkItemCopier.Copy(source, overrideParentId: true, parentIdValue: null);

        copy.ParentId.ShouldBeNull();
    }

    [Fact]
    public void Copy_OverrideParentIdFalse_PreservesSourceParent()
    {
        var source = BuildFullSource();

        var copy = WorkItemCopier.Copy(source, overrideParentId: false, parentIdValue: 999);

        copy.ParentId.ShouldBe(source.ParentId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  isSeedOverride
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_IsSeedOverrideTrue_SetsSeedFlag()
    {
        var source = new WorkItemBuilder(1, "Test").Build();
        source.IsSeed.ShouldBeFalse();

        var copy = WorkItemCopier.Copy(source, isSeedOverride: true);

        copy.IsSeed.ShouldBeTrue();
    }

    [Fact]
    public void Copy_IsSeedOverrideFalse_ClearsSeedFlag()
    {
        var source = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        source.IsSeed.ShouldBeTrue();

        var copy = WorkItemCopier.Copy(source, isSeedOverride: false);

        copy.IsSeed.ShouldBeFalse();
    }

    [Fact]
    public void Copy_IsSeedOverrideNull_PreservesSourceFlag()
    {
        var source = new WorkItemBuilder(-1, "Seed").AsSeed().Build();

        var copy = WorkItemCopier.Copy(source);

        copy.IsSeed.ShouldBe(source.IsSeed);
    }

    // ═══════════════════════════════════════════════════════════════
    //  fieldsOverride + preserveExistingFields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_FieldsOverride_PreserveExisting_MergesFields()
    {
        var source = new WorkItemBuilder(-1, "Test")
            .AsSeed()
            .WithField("System.Description", "original")
            .WithField("Custom.Keep", "kept")
            .Build();

        var overrides = new Dictionary<string, string?> { ["System.Description"] = "overridden" };

        var copy = WorkItemCopier.Copy(source, fieldsOverride: overrides, preserveExistingFields: true);

        copy.Fields["System.Description"].ShouldBe("overridden");
        copy.Fields["Custom.Keep"].ShouldBe("kept");
        copy.Fields.Count.ShouldBe(2);
    }

    [Fact]
    public void Copy_FieldsOverride_DoNotPreserve_ReplacesFields()
    {
        var source = new WorkItemBuilder(-1, "Test")
            .AsSeed()
            .WithField("System.Description", "original")
            .WithField("Custom.Keep", "kept")
            .Build();

        var overrides = new Dictionary<string, string?> { ["Custom.New"] = "new-value" };

        var copy = WorkItemCopier.Copy(source, fieldsOverride: overrides, preserveExistingFields: false);

        copy.Fields.ShouldContainKey("Custom.New");
        copy.Fields["Custom.New"].ShouldBe("new-value");
        copy.Fields.ShouldNotContainKey("System.Description");
        copy.Fields.ShouldNotContainKey("Custom.Keep");
        copy.Fields.Count.ShouldBe(1);
    }

    [Fact]
    public void Copy_FieldsOverrideNull_PreservesSourceFields()
    {
        var source = new WorkItemBuilder(-1, "Test")
            .AsSeed()
            .WithField("System.Description", "desc")
            .Build();

        var copy = WorkItemCopier.Copy(source, fieldsOverride: null);

        copy.Fields["System.Description"].ShouldBe("desc");
    }

    // ═══════════════════════════════════════════════════════════════
    //  preserveDirty
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_PreserveDirtyTrue_TransfersDirtyFlag()
    {
        var source = new WorkItemBuilder(-1, "Dirty").AsSeed().Build();
        source.UpdateField("System.Description", "changed");
        source.IsDirty.ShouldBeTrue();

        var copy = WorkItemCopier.Copy(source, preserveDirty: true);

        copy.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Copy_PreserveDirtyTrue_CleanSourceStaysClean()
    {
        var source = new WorkItemBuilder(-1, "Clean").AsSeed().Build();
        source.IsDirty.ShouldBeFalse();

        var copy = WorkItemCopier.Copy(source, preserveDirty: true);

        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Copy_PreserveDirtyFalse_ProducesCleanCopy()
    {
        var source = new WorkItemBuilder(-1, "Dirty").AsSeed().Build();
        source.UpdateField("System.Description", "changed");
        source.IsDirty.ShouldBeTrue();

        var copy = WorkItemCopier.Copy(source, preserveDirty: false);

        copy.IsDirty.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Revision handling
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_RevisionGreaterThanZero_CallsMarkSynced()
    {
        var source = new WorkItemBuilder(1, "Synced").Build();
        source.MarkSynced(42);

        var copy = WorkItemCopier.Copy(source);

        copy.Revision.ShouldBe(42);
        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Copy_RevisionZero_SkipsMarkSynced()
    {
        var source = new WorkItemBuilder(-1, "Unsynced").AsSeed().Build();
        source.Revision.ShouldBe(0);

        var copy = WorkItemCopier.Copy(source);

        copy.Revision.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Immutability — copy does not mutate source
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_DoesNotMutateSource()
    {
        var source = BuildFullSource();
        var originalTitle = source.Title;
        var originalParentId = source.ParentId;

        _ = WorkItemCopier.Copy(source,
            titleOverride: "Changed",
            overrideParentId: true,
            parentIdValue: 999,
            isSeedOverride: false);

        source.Title.ShouldBe(originalTitle);
        source.ParentId.ShouldBe(originalParentId);
        source.IsSeed.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Combined overrides (matches With* method semantics)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Copy_MatchesWithSeedFieldsSemantics()
    {
        var source = BuildFullSource();
        var newFields = new Dictionary<string, string?> { ["Custom.New"] = "value" };

        var viaCopier = WorkItemCopier.Copy(source,
            titleOverride: "New Title",
            fieldsOverride: newFields,
            preserveExistingFields: false,
            preserveDirty: false);

        var viaWith = source.WithSeedFields("New Title", newFields);

        viaCopier.Id.ShouldBe(viaWith.Id);
        viaCopier.Title.ShouldBe(viaWith.Title);
        viaCopier.IsDirty.ShouldBe(viaWith.IsDirty);
        viaCopier.Fields.Count.ShouldBe(viaWith.Fields.Count);
    }

    [Fact]
    public void Copy_MatchesWithParentIdSemantics()
    {
        var source = BuildFullSource();
        source.UpdateField("System.Description", "dirty");

        var viaCopier = WorkItemCopier.Copy(source,
            overrideParentId: true,
            parentIdValue: 999,
            preserveDirty: true);

        var viaWith = source.WithParentId(999);

        viaCopier.ParentId.ShouldBe(viaWith.ParentId);
        viaCopier.IsDirty.ShouldBe(viaWith.IsDirty);
        viaCopier.Fields.Count.ShouldBe(viaWith.Fields.Count);
    }

    [Fact]
    public void Copy_MatchesWithIsSeedSemantics()
    {
        var source = BuildFullSource();

        var viaCopier = WorkItemCopier.Copy(source,
            isSeedOverride: false,
            preserveDirty: false);

        var viaWith = source.WithIsSeed(false);

        viaCopier.IsSeed.ShouldBe(viaWith.IsSeed);
        viaCopier.IsDirty.ShouldBe(viaWith.IsDirty);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem BuildFullSource()
    {
        var item = new WorkItem
        {
            Id = -42,
            Type = WorkItemType.Feature,
            Title = "Full Source",
            State = "Active",
            AssignedTo = "test@example.com",
            IterationPath = IterationPath.Parse(@"TestProject\Sprint7").Value,
            AreaPath = AreaPath.Parse(@"TestProject\Backend").Value,
            ParentId = 100,
            IsSeed = true,
            SeedCreatedAt = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
            LastSyncedAt = new DateTimeOffset(2025, 6, 16, 8, 0, 0, TimeSpan.Zero),
        };

        item.MarkSynced(42);
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "desc",
            ["Custom.TestField"] = "test-value",
        });

        return item;
    }
}
