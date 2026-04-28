using Shouldly;
using Twig.Domain.Services.Field;
using Xunit;

namespace Twig.Domain.Tests.Services.Field;

public class CacheAgeFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 14, 12, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════════════
    //  Null input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_NullLastSyncedAt_ReturnsNull()
    {
        CacheAgeFormatter.Format(null, 5, Now).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Within stale threshold (fresh)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_WithinThreshold_ReturnsNull()
    {
        var lastSynced = Now.AddMinutes(-4);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBeNull();
    }

    [Fact]
    public void Format_ExactlyAtThreshold_ReturnsNull()
    {
        // At exactly the boundary (4.999... < 5), still fresh
        var lastSynced = Now.AddMinutes(-4).AddSeconds(-59);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBeNull();
    }

    [Fact]
    public void Format_JustSynced_ReturnsNull()
    {
        CacheAgeFormatter.Format(Now, 5, Now).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Minutes formatting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_StaleByMinutes_ReturnsMinutesFormat()
    {
        var lastSynced = Now.AddMinutes(-7);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 7m ago)");
    }

    [Fact]
    public void Format_StaleByExactlyThreshold_ReturnsMinutesFormat()
    {
        var lastSynced = Now.AddMinutes(-5);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 5m ago)");
    }

    [Fact]
    public void Format_StaleBy59Minutes_ReturnsMinutesFormat()
    {
        var lastSynced = Now.AddMinutes(-59);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 59m ago)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Hours formatting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_StaleBy60Minutes_ReturnsHoursFormat()
    {
        var lastSynced = Now.AddMinutes(-60);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 1h ago)");
    }

    [Fact]
    public void Format_StaleByMultipleHours_ReturnsHoursFormat()
    {
        var lastSynced = Now.AddHours(-2).AddMinutes(-30);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 2h ago)");
    }

    [Fact]
    public void Format_StaleBy23Hours_ReturnsHoursFormat()
    {
        var lastSynced = Now.AddHours(-23).AddMinutes(-59);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 23h ago)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Days formatting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_StaleBy24Hours_ReturnsDaysFormat()
    {
        var lastSynced = Now.AddHours(-24);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 1d ago)");
    }

    [Fact]
    public void Format_StaleByMultipleDays_ReturnsDaysFormat()
    {
        var lastSynced = Now.AddDays(-3);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 3d ago)");
    }

    [Fact]
    public void Format_StaleByManyDays_ReturnsDaysFormat()
    {
        var lastSynced = Now.AddDays(-30);

        CacheAgeFormatter.Format(lastSynced, 5, Now).ShouldBe("(cached 30d ago)");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Format_ZeroStaleMinutes_AnyElapsedTimeIsStale()
    {
        var lastSynced = Now.AddSeconds(-30);

        // 0.5 minutes elapsed, staleMinutes=0 → stale, but truncates to 0m
        CacheAgeFormatter.Format(lastSynced, 0, Now).ShouldBe("(cached 0m ago)");
    }

    [Fact]
    public void Format_LargeStaleThreshold_FreshWithinThreshold()
    {
        var lastSynced = Now.AddMinutes(-59);

        CacheAgeFormatter.Format(lastSynced, 60, Now).ShouldBeNull();
    }

    [Theory]
    [InlineData(-15, 10, "(cached 15m ago)")]
    [InlineData(-120, 30, "(cached 2h ago)")]
    [InlineData(-1440, 60, "(cached 1d ago)")]
    public void Format_VariousScenarios_ReturnsExpected(int minutesAgo, int staleMinutes, string expected)
    {
        var lastSynced = Now.AddMinutes(minutesAgo);

        CacheAgeFormatter.Format(lastSynced, staleMinutes, Now).ShouldBe(expected);
    }

    [Fact]
    public void Format_PublicOverload_DoesNotThrow()
    {
        // Smoke test: the public overload uses DateTimeOffset.UtcNow internally
        var lastSynced = DateTimeOffset.UtcNow.AddHours(-2);

        var result = CacheAgeFormatter.Format(lastSynced, 5);

        result.ShouldNotBeNull();
        result.ShouldStartWith("(cached ");
        result.ShouldEndWith(" ago)");
    }
}
