using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class TrackedItemTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new TrackedItem(42, "single", now);

        item.Id.ShouldBe(42);
        item.TrackingMode.ShouldBe("single");
        item.CreatedAt.ShouldBe(now);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, "single", now);
        var b = new TrackedItem(1, "single", now);
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentId_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, "single", now);
        var b = new TrackedItem(2, "single", now);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentMode_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, "single", now);
        var b = new TrackedItem(1, "tree", now);
        a.ShouldNotBe(b);
    }
}
