using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class StepOutputStoreTests
{
    // ── Construction ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroCapacity_CreatesEmptyStore()
    {
        var store = new StepOutputStore(0);
        store.Capacity.ShouldBe(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void Constructor_ValidCapacity_SetsCapacity(int capacity)
    {
        var store = new StepOutputStore(capacity);
        store.Capacity.ShouldBe(capacity);
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new StepOutputStore(-1));
    }

    // ── Record + GetResult ───────────────────────────────────────────

    [Fact]
    public void Record_ValidIndex_StoresResult()
    {
        var store = new StepOutputStore(3);
        var result = Succeeded(1, """{"id": 42}""");

        store.Record(1, result);

        store.GetResult(1).ShouldBe(result);
    }

    [Fact]
    public void Record_FirstSlot_RetrievableViaGetResult()
    {
        var store = new StepOutputStore(1);
        var result = Succeeded(0, """{"ok": true}""");

        store.Record(0, result);

        store.GetResult(0).ShouldBe(result);
    }

    [Fact]
    public void Record_LastSlot_RetrievableViaGetResult()
    {
        var store = new StepOutputStore(5);
        var result = Succeeded(4, """{"ok": true}""");

        store.Record(4, result);

        store.GetResult(4).ShouldBe(result);
    }

    [Fact]
    public void GetResult_UnrecordedSlot_ReturnsNull()
    {
        var store = new StepOutputStore(3);
        store.GetResult(0).ShouldBeNull();
        store.GetResult(1).ShouldBeNull();
        store.GetResult(2).ShouldBeNull();
    }

    [Fact]
    public void Record_NullResult_Throws()
    {
        var store = new StepOutputStore(1);
        Should.Throw<ArgumentNullException>(() => store.Record(0, null!));
    }

    [Fact]
    public void Record_IndexOutOfRange_Throws()
    {
        var store = new StepOutputStore(3);
        Should.Throw<ArgumentOutOfRangeException>(() => store.Record(3, Succeeded(3, "{}")));
        Should.Throw<ArgumentOutOfRangeException>(() => store.Record(-1, Succeeded(0, "{}")));
    }

    [Fact]
    public void Record_ZeroCapacityStore_ThrowsWithClearMessage()
    {
        var store = new StepOutputStore(0);
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => store.Record(0, Succeeded(0, "{}")));
        ex.Message.ShouldContain("zero steps");
    }

    [Fact]
    public void GetResult_IndexOutOfRange_Throws()
    {
        var store = new StepOutputStore(3);
        Should.Throw<ArgumentOutOfRangeException>(() => store.GetResult(3));
        Should.Throw<ArgumentOutOfRangeException>(() => store.GetResult(-1));
    }

    [Fact]
    public void GetResult_ZeroCapacityStore_ThrowsWithClearMessage()
    {
        var store = new StepOutputStore(0);
        var ex = Should.Throw<ArgumentOutOfRangeException>(() => store.GetResult(0));
        ex.Message.ShouldContain("zero steps");
    }

    // ── GetSnapshot ──────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReturnsArrayOfCorrectLength()
    {
        var store = new StepOutputStore(3);
        store.GetSnapshot().Length.ShouldBe(3);
    }

    [Fact]
    public void GetSnapshot_ReflectsRecordedResults()
    {
        var store = new StepOutputStore(3);
        var r0 = Succeeded(0, """{"a": 1}""");
        var r2 = Failed(2);

        store.Record(0, r0);
        store.Record(2, r2);

        var snapshot = store.GetSnapshot();
        snapshot[0].ShouldBe(r0);
        snapshot[1].ShouldBeNull();
        snapshot[2].ShouldBe(r2);
    }

    [Fact]
    public void GetSnapshot_ReturnsIndependentCopy()
    {
        var store = new StepOutputStore(2);
        store.Record(0, Succeeded(0, "{}"));

        var snapshot = store.GetSnapshot();
        snapshot[0] = null; // Mutate the snapshot

        // Store should be unaffected.
        store.GetResult(0).ShouldNotBeNull();
    }

    [Fact]
    public void GetSnapshot_EmptyStore_ReturnsAllNulls()
    {
        var store = new StepOutputStore(3);
        var snapshot = store.GetSnapshot();

        snapshot.ShouldAllBe(r => r == null);
    }

    // ── FillSkipped ──────────────────────────────────────────────────

    [Fact]
    public void FillSkipped_StepNode_FillsNullSlot()
    {
        var store = new StepOutputStore(1);
        var step = new StepNode(0, "twig_status", new Dictionary<string, object?>());

        store.FillSkipped(step, "test reason");

        var result = store.GetResult(0);
        result.ShouldNotBeNull();
        result.Status.ShouldBe(StepStatus.Skipped);
        result.Error.ShouldBe("test reason");
        result.ToolName.ShouldBe("twig_status");
    }

    [Fact]
    public void FillSkipped_StepNode_DoesNotOverwriteExistingResult()
    {
        var store = new StepOutputStore(1);
        var existing = Succeeded(0, """{"id": 1}""");
        store.Record(0, existing);

        var step = new StepNode(0, "twig_status", new Dictionary<string, object?>());
        store.FillSkipped(step, "should not overwrite");

        store.GetResult(0).ShouldBe(existing);
    }

    [Fact]
    public void FillSkipped_SequenceNode_FillsAllNullSlots()
    {
        var store = new StepOutputStore(3);
        store.Record(0, Succeeded(0, "{}"));

        var sequence = new SequenceNode(
        [
            new StepNode(0, "tool_a", new Dictionary<string, object?>()),
            new StepNode(1, "tool_b", new Dictionary<string, object?>()),
            new StepNode(2, "tool_c", new Dictionary<string, object?>())
        ]);

        store.FillSkipped(sequence, "skipped");

        store.GetResult(0)!.Status.ShouldBe(StepStatus.Succeeded); // Preserved
        store.GetResult(1)!.Status.ShouldBe(StepStatus.Skipped);
        store.GetResult(2)!.Status.ShouldBe(StepStatus.Skipped);
    }

    [Fact]
    public void FillSkipped_ParallelNode_FillsAllNullSlots()
    {
        var store = new StepOutputStore(2);

        var parallel = new ParallelNode(
        [
            new StepNode(0, "tool_a", new Dictionary<string, object?>()),
            new StepNode(1, "tool_b", new Dictionary<string, object?>())
        ]);

        store.FillSkipped(parallel, "timeout");

        store.GetResult(0)!.Status.ShouldBe(StepStatus.Skipped);
        store.GetResult(0)!.Error.ShouldBe("timeout");
        store.GetResult(1)!.Status.ShouldBe(StepStatus.Skipped);
    }

    [Fact]
    public void FillSkipped_NestedTree_FillsAllNullSlots()
    {
        var store = new StepOutputStore(4);
        store.Record(0, Succeeded(0, "{}"));

        var tree = new SequenceNode(
        [
            new StepNode(0, "tool_a", new Dictionary<string, object?>()),
            new ParallelNode(
            [
                new StepNode(1, "tool_b", new Dictionary<string, object?>()),
                new SequenceNode(
                [
                    new StepNode(2, "tool_c", new Dictionary<string, object?>()),
                    new StepNode(3, "tool_d", new Dictionary<string, object?>())
                ])
            ])
        ]);

        store.FillSkipped(tree, "skipped");

        store.GetResult(0)!.Status.ShouldBe(StepStatus.Succeeded);
        store.GetResult(1)!.Status.ShouldBe(StepStatus.Skipped);
        store.GetResult(2)!.Status.ShouldBe(StepStatus.Skipped);
        store.GetResult(3)!.Status.ShouldBe(StepStatus.Skipped);
    }

    // ── ToResultList ─────────────────────────────────────────────────

    [Fact]
    public void ToResultList_AllSlotsFilled_ReturnsOrderedList()
    {
        var store = new StepOutputStore(3);
        var r0 = Succeeded(0, """{"a": 1}""");
        var r1 = Failed(1);
        var r2 = Skipped(2);

        store.Record(0, r0);
        store.Record(1, r1);
        store.Record(2, r2);

        var list = store.ToResultList();

        list.Count.ShouldBe(3);
        list[0].ShouldBe(r0);
        list[1].ShouldBe(r1);
        list[2].ShouldBe(r2);
    }

    [Fact]
    public void ToResultList_NullSlot_Throws()
    {
        var store = new StepOutputStore(2);
        store.Record(0, Succeeded(0, "{}"));
        // Slot 1 intentionally left null.

        Should.Throw<InvalidOperationException>(() => store.ToResultList());
    }

    [Fact]
    public void ToResultList_EmptyStore_ReturnsEmptyList()
    {
        var store = new StepOutputStore(0);
        var list = store.ToResultList();
        list.Count.ShouldBe(0);
    }

    // ── Multiple slots independence ──────────────────────────────────

    [Fact]
    public void Record_MultipleSlots_DoNotInterfere()
    {
        var store = new StepOutputStore(3);
        var r0 = Succeeded(0, """{"step": 0}""");
        var r1 = Succeeded(1, """{"step": 1}""");
        var r2 = Failed(2);

        store.Record(0, r0);
        store.Record(1, r1);
        store.Record(2, r2);

        store.GetResult(0).ShouldBe(r0);
        store.GetResult(1).ShouldBe(r1);
        store.GetResult(2).ShouldBe(r2);
    }

    // ── Concurrent read safety ───────────────────────────────────────

    [Fact]
    public async Task ConcurrentReads_AfterRecord_AllSeeResult()
    {
        var store = new StepOutputStore(1);
        var result = Succeeded(0, """{"id": 42}""");
        store.Record(0, result);

        // Launch multiple concurrent reads.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => store.GetResult(0)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r == result);
    }

    [Fact]
    public async Task ConcurrentSnapshots_ReturnConsistentData()
    {
        var store = new StepOutputStore(3);
        store.Record(0, Succeeded(0, """{"a": 1}"""));
        store.Record(1, Succeeded(1, """{"b": 2}"""));

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => store.GetSnapshot()))
            .ToArray();

        var snapshots = await Task.WhenAll(tasks);

        foreach (var snapshot in snapshots)
        {
            snapshot.Length.ShouldBe(3);
            snapshot[0]!.StepIndex.ShouldBe(0);
            snapshot[1]!.StepIndex.ShouldBe(1);
            snapshot[2].ShouldBeNull();
        }
    }
}
