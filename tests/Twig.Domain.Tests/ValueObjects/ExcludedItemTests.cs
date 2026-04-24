using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class ExcludedItemTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ExcludedItem(42, "no longer relevant", now);

        item.WorkItemId.ShouldBe(42);
        item.Reason.ShouldBe("no longer relevant");
        item.ExcludedAt.ShouldBe(now);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ExcludedItem(1, "reason", now);
        var b = new ExcludedItem(1, "reason", now);
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentId_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ExcludedItem(1, "reason", now);
        var b = new ExcludedItem(2, "reason", now);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentReason_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ExcludedItem(1, "reason A", now);
        var b = new ExcludedItem(1, "reason B", now);
        a.ShouldNotBe(b);
    }
}
