using System.Collections.Concurrent;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

public sealed class SeedIdCounterTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Initialize
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Initialize_NegativeMinExistingId_ClampsCounterToThatValue()
    {
        var counter = new SeedIdCounter();

        counter.Initialize(-10);

        // Next should return one below -10
        counter.Next().ShouldBe(-11);
    }

    [Fact]
    public void Initialize_PositiveMinExistingId_ClampsCounterToZero()
    {
        var counter = new SeedIdCounter();

        counter.Initialize(5);

        // Positive values are clamped to 0, so Next returns -1
        counter.Next().ShouldBe(-1);
    }

    [Fact]
    public void Initialize_Zero_ClampsCounterToZero()
    {
        var counter = new SeedIdCounter();

        counter.Initialize(0);

        counter.Next().ShouldBe(-1);
    }

    [Fact]
    public void Initialize_CalledMultipleTimes_LastCallWins()
    {
        var counter = new SeedIdCounter();

        counter.Initialize(-20);
        counter.Initialize(-5);

        counter.Next().ShouldBe(-6);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Next — sequential behavior
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Next_ReturnsDecrementingNegativeIds()
    {
        var counter = new SeedIdCounter();

        var id1 = counter.Next();
        var id2 = counter.Next();
        var id3 = counter.Next();

        id1.ShouldBe(-1);
        id2.ShouldBe(-2);
        id3.ShouldBe(-3);
    }

    [Fact]
    public void Next_AfterInitialize_StartsFromInitializedValue()
    {
        var counter = new SeedIdCounter();
        counter.Initialize(-100);

        var id1 = counter.Next();
        var id2 = counter.Next();

        id1.ShouldBe(-101);
        id2.ShouldBe(-102);
    }

    [Fact]
    public void Next_MultipleCalls_ProduceUniqueIds()
    {
        var counter = new SeedIdCounter();
        var ids = new HashSet<int>();

        for (var i = 0; i < 1000; i++)
        {
            ids.Add(counter.Next()).ShouldBeTrue($"Duplicate ID detected at iteration {i}");
        }

        ids.Count.ShouldBe(1000);
    }

    [Fact]
    public void Next_AllIdsAreNegative()
    {
        var counter = new SeedIdCounter();

        for (var i = 0; i < 100; i++)
        {
            counter.Next().ShouldBeLessThan(0);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Thread-safety
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Next_ParallelCalls_ProduceNoDuplicateIds()
    {
        var counter = new SeedIdCounter();
        var ids = new ConcurrentBag<int>();
        const int threadCount = 100;
        const int idsPerThread = 100;

        Parallel.For(0, threadCount, _ =>
        {
            for (var i = 0; i < idsPerThread; i++)
            {
                ids.Add(counter.Next());
            }
        });

        var distinct = ids.Distinct().ToList();
        distinct.Count.ShouldBe(threadCount * idsPerThread);
    }

    [Fact]
    public void Next_ParallelCalls_AllIdsAreNegative()
    {
        var counter = new SeedIdCounter();
        var ids = new ConcurrentBag<int>();

        Parallel.For(0, 1000, _ =>
        {
            ids.Add(counter.Next());
        });

        ids.ShouldAllBe(id => id < 0);
    }

    [Fact]
    public void Next_ParallelCalls_AfterInitialize_AllIdsBelowInitializedValue()
    {
        var counter = new SeedIdCounter();
        counter.Initialize(-50);
        var ids = new ConcurrentBag<int>();

        Parallel.For(0, 500, _ =>
        {
            ids.Add(counter.Next());
        });

        ids.ShouldAllBe(id => id < -50);
        ids.Distinct().Count().ShouldBe(500);
    }
}
