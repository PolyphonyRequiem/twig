using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class RecoverableWorkItemDraftContractTests
{
    [Fact]
    public void StableIdentityExcludesRevision_AndQualifiesEveryNativeScope()
    {
        var key = Key();
        key.ShouldBe(Key());
        key.ShouldNotBe(new QualifiedWorkItemKey(@"C:\other", "connection", "binding", 42));
        key.ShouldNotBe(new QualifiedWorkItemKey(@"C:\repo", "other", "binding", 42));
        key.ShouldNotBe(new QualifiedWorkItemKey(@"C:\repo", "connection", "other", 42));
        key.ShouldNotBe(new QualifiedWorkItemKey(@"C:\repo", "connection", "binding", 43));
        RecoverableWorkItemDraft.Create(key, 7).ItemKey
            .ShouldBe(RecoverableWorkItemDraft.Create(key, 8).ItemKey);
    }

    [Fact]
    public void ObservationAdvancesGenerationWithoutRebasingOrDroppingIntent()
    {
        var original = RecoverableWorkItemDraft.Create(Key(), 7);
        var edited = original.SetField("System.Title", "before", "mine");
        var changedAgain = edited.SetField("system.title", "ignored-new-baseline", "mine again");
        var conflicts = new[] { new DraftFieldConflict("system.title", "before", "server", "mine again") };
        var observed = changedAgain.Observe(8, conflicts);

        observed.Generation.ShouldBe(3);
        observed.BaselineRevision.ShouldBe(7);
        observed.ObservedRevision.ShouldBe(8);
        observed.HasRevisionDrift.ShouldBeTrue();
        observed.HasConflicts.ShouldBeTrue();
        observed.Changes.ShouldBe([new DraftFieldChange("system.title", "before", "mine again")]);
        observed.Conflicts.ShouldBe(conflicts);
        observed.Observe(8, conflicts).ShouldBeSameAs(observed);
        original.Generation.ShouldBe(0);
        original.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void ReturningToBaselineRemovesEffectButAdvancesGeneration()
    {
        var edited = RecoverableWorkItemDraft.Create(Key(), 3).SetField("field", "old", "new");
        var reverted = edited.SetField("field", "ignored", "old");

        reverted.Generation.ShouldBe(2);
        reverted.Changes.ShouldBeEmpty();
        reverted.BaselineRevision.ShouldBe(3);
    }

    [Fact]
    public void NullAndEmptyValuesRemainDistinct_AndInputCollectionsAreCopied()
    {
        var changes = new[] { new DraftFieldChange("field", null, string.Empty) };
        var conflicts = new[] { new DraftFieldConflict("field", null, "server", string.Empty) };
        var draft = new RecoverableWorkItemDraft(Key(), 2, 3, 5, changes, conflicts);
        changes[0] = new DraftFieldChange("other", "a", "b");
        conflicts[0] = new DraftFieldConflict("other", "a", "b", "c");

        draft.Changes.ShouldHaveSingleItem().BaselineValue.ShouldBeNull();
        draft.Changes[0].ProposedValue.ShouldBe(string.Empty);
        draft.Conflicts.ShouldHaveSingleItem().FieldRef.ShouldBe("field");
        draft.Conflicts[0].ProposedValue.ShouldBe(string.Empty);
    }

    private static QualifiedWorkItemKey Key() => new(@"C:\repo", "connection", "binding", 42);
}
