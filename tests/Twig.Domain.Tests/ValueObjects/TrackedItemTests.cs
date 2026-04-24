using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class TrackedItemTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new TrackedItem(42, TrackingMode.Single, now);

        item.WorkItemId.ShouldBe(42);
        item.Mode.ShouldBe(TrackingMode.Single);
        item.TrackedAt.ShouldBe(now);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, TrackingMode.Single, now);
        var b = new TrackedItem(1, TrackingMode.Single, now);
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentId_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, TrackingMode.Single, now);
        var b = new TrackedItem(2, TrackingMode.Single, now);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Equality_DifferentMode_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TrackedItem(1, TrackingMode.Single, now);
        var b = new TrackedItem(1, TrackingMode.Tree, now);
        a.ShouldNotBe(b);
    }
}
