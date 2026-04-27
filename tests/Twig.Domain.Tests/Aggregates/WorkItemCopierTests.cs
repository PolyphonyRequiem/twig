using System.Collections.ObjectModel;
using System.Reflection;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

/// <summary>
/// Reflection-based theory test that verifies all three <see cref="WorkItem"/> copy methods
/// (<see cref="WorkItem.WithSeedFields"/>, <see cref="WorkItem.WithParentId"/>,
/// <see cref="WorkItem.WithIsSeed"/>) preserve every public property or explicitly override it.
/// Adding a new <c>init</c> property to <see cref="WorkItem"/> without updating copy logic
/// causes an assertion failure — a compile-enforced contract.
/// </summary>
public sealed class WorkItemCopierTests
{
    private static readonly PropertyInfo[] AllPublicProperties =
        typeof(WorkItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    public static IEnumerable<object[]> CopyMethodTestCases()
    {
        yield return ["WithSeedFields"];
        yield return ["WithParentId"];
        yield return ["WithIsSeed"];
    }

    [Theory]
    [MemberData(nameof(CopyMethodTestCases))]
    public void WorkItem_Copy_Preserves_All_Properties(string copyMethodName)
    {
        // Arrange: source with every property set to a non-default, distinguishable value
        var source = BuildFullyPopulatedWorkItem();
        source.IsDirty.ShouldBeTrue("source must be dirty to test IsDirty preservation");
        source.PendingNotes.Count.ShouldBeGreaterThan(0, "source must have pending notes");

        // Act: invoke the copy method and get the set of known overrides
        var (copy, knownOverrides) = InvokeCopy(source, copyMethodName);

        // Assert: every public property on the copy either matches the source
        // or is a documented override for this copy method
        foreach (var prop in AllPublicProperties)
        {
            if (knownOverrides.TryGetValue(prop.Name, out var expected))
            {
                AssertProperty(copy, expected, prop, copyMethodName, isOverride: true);
            }
            else
            {
                AssertProperty(copy, prop.GetValue(source), prop, copyMethodName, isOverride: false);
            }
        }
    }

    // ── Source builder ───────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="WorkItem"/> with every public property set to a non-default
    /// value. If any property remains at its default, the preservation test cannot detect
    /// a missing copy — so each value is chosen to be obviously non-default.
    /// </summary>
    private static WorkItem BuildFullyPopulatedWorkItem()
    {
        var item = new WorkItem
        {
            Id = -42,
            Type = WorkItemType.Feature,
            Title = "Fully Populated Source",
            State = "Active",
            AssignedTo = "test@example.com",
            IterationPath = IterationPath.Parse(@"TestProject\Sprint7").Value,
            AreaPath = AreaPath.Parse(@"TestProject\Backend").Value,
            ParentId = 100,
            IsSeed = true,
            SeedCreatedAt = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero),
            LastSyncedAt = new DateTimeOffset(2025, 6, 16, 8, 0, 0, TimeSpan.Zero),
        };

        // Revision = 42 (non-default) via MarkSynced, which also clears IsDirty
        item.MarkSynced(42);

        // Populate fields (non-empty collection)
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Custom.TestField"] = "test-value",
        });

        // Make dirty (non-default) — must come after MarkSynced which clears it
        item.UpdateField("System.Description", "A description");

        // Add a pending note (non-empty collection)
        item.AddNote(new PendingNote("A test note", new DateTimeOffset(2025, 6, 17, 10, 0, 0, TimeSpan.Zero), false));

        return item;
    }

    // ── Copy dispatch ───────────────────────────────────────────────

    /// <summary>
    /// Invokes the named copy method on <paramref name="source"/> and returns the copy
    /// along with a dictionary of property names → expected values for properties that
    /// the method intentionally overrides (rather than preserving from the source).
    /// </summary>
    private static (WorkItem Copy, Dictionary<string, object?> KnownOverrides) InvokeCopy(
        WorkItem source, string methodName)
    {
        return methodName switch
        {
            "WithSeedFields" => InvokeWithSeedFields(source),
            "WithParentId" => InvokeWithParentId(source),
            "WithIsSeed" => InvokeWithIsSeed(source),
            _ => throw new ArgumentException($"Unknown copy method: {methodName}"),
        };
    }

    private static (WorkItem, Dictionary<string, object?>) InvokeWithSeedFields(WorkItem source)
    {
        const string newTitle = "Overridden Title";
        var newFields = new Dictionary<string, string?> { ["Custom.NewField"] = "new-value" };

        var copy = source.WithSeedFields(newTitle, newFields);

        // WithSeedFields overrides: Title, Fields, IsDirty (always clean), PendingNotes (not copied)
        var overrides = new Dictionary<string, object?>
        {
            ["Title"] = newTitle,
            ["IsDirty"] = false,
            ["PendingNotes"] = Array.Empty<PendingNote>(),
            ["Fields"] = new ReadOnlyDictionary<string, string?>(newFields),
        };

        return (copy, overrides);
    }

    private static (WorkItem, Dictionary<string, object?>) InvokeWithParentId(WorkItem source)
    {
        const int newParentId = 999;

        var copy = source.WithParentId(newParentId);

        // WithParentId overrides: ParentId, PendingNotes (not copied)
        // IsDirty is PRESERVED (not overridden)
        var overrides = new Dictionary<string, object?>
        {
            ["ParentId"] = (int?)newParentId,
            ["PendingNotes"] = Array.Empty<PendingNote>(),
        };

        return (copy, overrides);
    }

    private static (WorkItem, Dictionary<string, object?>) InvokeWithIsSeed(WorkItem source)
    {
        const bool newIsSeed = false;

        var copy = source.WithIsSeed(newIsSeed);

        // WithIsSeed overrides: IsSeed, IsDirty (always clean), PendingNotes (not copied)
        var overrides = new Dictionary<string, object?>
        {
            ["IsSeed"] = newIsSeed,
            ["IsDirty"] = false,
            ["PendingNotes"] = Array.Empty<PendingNote>(),
        };

        return (copy, overrides);
    }

    // ── Assertion helpers ───────────────────────────────────────────

    /// <summary>
    /// Asserts a single property value, with special handling for collection-typed
    /// properties (<see cref="WorkItem.Fields"/> and <see cref="WorkItem.PendingNotes"/>)
    /// that require content-level comparison rather than reference equality.
    /// </summary>
    private static void AssertProperty(
        WorkItem copy,
        object? expected,
        PropertyInfo prop,
        string methodName,
        bool isOverride)
    {
        var label = isOverride ? "overridden" : "preserved";
        var actual = prop.GetValue(copy);

        if (prop.Name == "Fields")
        {
            AssertFieldsEqual(
                (IReadOnlyDictionary<string, string?>)actual!,
                (IReadOnlyDictionary<string, string?>)expected!,
                methodName,
                label);
            return;
        }

        if (prop.Name == "PendingNotes")
        {
            var actualNotes = (IReadOnlyList<PendingNote>)actual!;
            var expectedNotes = expected as IReadOnlyList<PendingNote>
                ?? (IReadOnlyList<PendingNote>)(expected as PendingNote[] ?? []);
            actualNotes.Count.ShouldBe(expectedNotes.Count,
                $"PendingNotes count should be {label} by {methodName}");
            for (var i = 0; i < expectedNotes.Count; i++)
                actualNotes[i].ShouldBe(expectedNotes[i]);
            return;
        }

        actual.ShouldBe(expected,
            $"Property '{prop.Name}' should be {label} by {methodName}");
    }

    private static void AssertFieldsEqual(
        IReadOnlyDictionary<string, string?> actual,
        IReadOnlyDictionary<string, string?> expected,
        string methodName,
        string label)
    {
        actual.Count.ShouldBe(expected.Count,
            $"Fields count should be {label} by {methodName}");

        foreach (var kvp in expected)
        {
            actual.ShouldContainKey(kvp.Key,
                $"Fields should contain '{kvp.Key}' ({label} by {methodName})");
            actual[kvp.Key].ShouldBe(kvp.Value,
                $"Fields['{kvp.Key}'] should be {label} by {methodName}");
        }
    }
}
