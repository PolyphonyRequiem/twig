using Shouldly;
using Twig.Browser;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserBenchIndexTests
{
    [Fact]
    public void TreeFoldsHideEveryDescendantButTableKeepsActualMembersAndUncachedPins()
    {
        var index = new BrowserBenchIndex([
            Item(41, null, member: false), Item(42, 41), Item(43, 42), Item(99, null, cached: false),
        ]);
        index.Visible("tree", new HashSet<int>()).Select(i => i.Id).ShouldBe(new[] { 41, 42, 43, 99 });
        var folded = new HashSet<int> { 41 };
        index.Visible("tree", folded).Select(i => i.Id).ShouldBe(new[] { 41, 99 });
        index.Visible("table", folded).Select(i => i.Id).ShouldBe(new[] { 42, 43, 99 });
    }

    [Fact]
    public void CachedHierarchyCycleRemainsSelectableAndFoldingCannotHideItsOwnDisclosure()
    {
        var index = new BrowserBenchIndex([Item(10, 11), Item(11, 10)]);
        index.Visible("tree", new HashSet<int>()).Select(i => i.Id).ShouldBe(new[] { 10, 11 });
        index.Visible("tree", new HashSet<int> { 10 }).Select(i => i.Id).ShouldBe(new[] { 10 });
    }

    private static BrowserBenchItem Item(int id, int? parent, bool member = true, bool cached = true)
        => new(id, parent, "Task", "Open", "Content", cached, member, false, false, false, false, []);
}
