using Bench.Application;
using Shouldly;
using Xunit;

namespace Twig.Server.Tests;

public sealed class CatalogChangesTests
{
    [Theory]
    [InlineData("content", "presentation")]
    [InlineData("presentation", "content")]
    [InlineData("content", "agent")]
    [InlineData("agent", "content")]
    [InlineData("presentation", "agent")]
    [InlineData("agent", "presentation")]
    [InlineData("agent", "agent")]
    [InlineData("content", "content")]
    public void OverflowIsBoundedAndRequiresAllResourceResync(string first, string second)
    {
        var changes = new CatalogChanges();
        using var subscription = changes.Subscribe(out var reader);
        Publish(first);
        Publish(second);
        reader.TryRead(out var result).ShouldBeTrue();
        result!.Resync.ShouldBeTrue();
        result.AgentViewId.ShouldBeNull();
        result.PresentationOnly.ShouldBeFalse();
        reader.TryRead(out _).ShouldBeFalse();

        void Publish(string kind)
        {
            if (kind == "agent") changes.PublishAgent(Guid.NewGuid());
            else if (kind == "presentation") changes.PublishPresentation();
            else changes.Publish(42);
        }
    }

    [Fact]
    public void ResyncSurvivesSustainedOverflowAndSubscriptionsRemainIndependent()
    {
        var changes = new CatalogChanges();
        using var slow = changes.Subscribe(out var slowReader);
        using var fast = changes.Subscribe(out var fastReader);
        for (var index = 0; index < 1000; index++)
        {
            changes.Publish(index);
            fastReader.TryRead(out var current).ShouldBeTrue();
            current!.Revision.ShouldBe(index);
            current.Resync.ShouldBeFalse();
        }
        slowReader.TryRead(out var pending).ShouldBeTrue();
        pending!.Resync.ShouldBeTrue();
        slowReader.TryRead(out _).ShouldBeFalse();
        slow.Dispose();
        slowReader.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
