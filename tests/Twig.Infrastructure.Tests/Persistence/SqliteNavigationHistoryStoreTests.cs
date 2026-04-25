using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqliteNavigationHistoryStore: record, back, forward, circular buffer, prune forward, clear.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteNavigationHistoryStoreTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteNavigationHistoryStore _navStore;

    public SqliteNavigationHistoryStoreTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _navStore = new SqliteNavigationHistoryStore(_store);
    }

    public void Dispose() => _store.Dispose();

    // ═══════════════════════════════════════════════════════════════
    //  RecordVisitAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecordVisitAsync_SingleEntry_AppearsInHistory()
    {
        await _navStore.RecordVisitAsync(42);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(1);
        entries[0].WorkItemId.ShouldBe(42);
        cursorId.ShouldBe(entries[0].Id);
    }

    [Fact]
    public async Task RecordVisitAsync_MultipleEntries_InChronologicalOrder()
    {
        await _navStore.RecordVisitAsync(1);
        await _navStore.RecordVisitAsync(2);
        await _navStore.RecordVisitAsync(3);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(3);
        entries[0].WorkItemId.ShouldBe(1);
        entries[1].WorkItemId.ShouldBe(2);
        entries[2].WorkItemId.ShouldBe(3);
        cursorId.ShouldBe(entries[2].Id);
    }

    [Fact]
    public async Task RecordVisitAsync_DuplicateConsecutiveIds_BothRecorded()
    {
        await _navStore.RecordVisitAsync(42);
        await _navStore.RecordVisitAsync(42);

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(2);
        entries[0].WorkItemId.ShouldBe(42);
        entries[1].WorkItemId.ShouldBe(42);
    }

    [Fact]
    public async Task RecordVisitAsync_NegativeSeedId_StoredAsIs()
    {
        await _navStore.RecordVisitAsync(-100);

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(1);
        entries[0].WorkItemId.ShouldBe(-100);
    }

    [Fact]
    public async Task RecordVisitAsync_StoresUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        await _navStore.RecordVisitAsync(42);
        var after = DateTimeOffset.UtcNow;

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries[0].VisitedAt.ShouldBeGreaterThanOrEqualTo(before);
        entries[0].VisitedAt.ShouldBeLessThanOrEqualTo(after);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Circular buffer (50-entry max)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecordVisitAsync_CircularBuffer_EnforcesMax50()
    {
        for (var i = 1; i <= 55; i++)
        {
            await _navStore.RecordVisitAsync(i);
        }

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(50);
        // Oldest 5 entries (IDs 1–5) should have been pruned
        entries[0].WorkItemId.ShouldBe(6);
        entries[^1].WorkItemId.ShouldBe(55);
    }

    [Fact]
    public async Task RecordVisitAsync_ExactlyAtMax_DoesNotPrune()
    {
        for (var i = 1; i <= 50; i++)
        {
            await _navStore.RecordVisitAsync(i);
        }

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(50);
        entries[0].WorkItemId.ShouldBe(1);
        entries[^1].WorkItemId.ShouldBe(50);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Forward pruning
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RecordVisitAsync_PrunesForwardEntries_WhenCursorNotAtHead()
    {
        // Record 3 entries: [A, B, C] cursor at C
        await _navStore.RecordVisitAsync(100);
        await _navStore.RecordVisitAsync(200);
        await _navStore.RecordVisitAsync(300);

        // Go back twice: cursor at A
        await _navStore.GoBackAsync();
        await _navStore.GoBackAsync();

        // Record new entry: forward entries (B, C) should be pruned
        await _navStore.RecordVisitAsync(999);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(2);
        entries[0].WorkItemId.ShouldBe(100);
        entries[1].WorkItemId.ShouldBe(999);
        cursorId.ShouldBe(entries[1].Id);
    }

    [Fact]
    public async Task RecordVisitAsync_PrunesForwardEntries_WhenCursorInMiddle()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);
        await _navStore.RecordVisitAsync(30);
        await _navStore.RecordVisitAsync(40);

        // Go back once: cursor at 30
        await _navStore.GoBackAsync();

        // Record new: 40 should be pruned
        await _navStore.RecordVisitAsync(50);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(4);
        entries[0].WorkItemId.ShouldBe(10);
        entries[1].WorkItemId.ShouldBe(20);
        entries[2].WorkItemId.ShouldBe(30);
        entries[3].WorkItemId.ShouldBe(50);
        cursorId.ShouldBe(entries[3].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GoBackAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoBackAsync_ReturnsNull_WhenEmpty()
    {
        var result = await _navStore.GoBackAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GoBackAsync_ReturnsNull_WhenAtOldestEntry()
    {
        await _navStore.RecordVisitAsync(42);
        var result = await _navStore.GoBackAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GoBackAsync_ReturnsPreviousWorkItemId()
    {
        await _navStore.RecordVisitAsync(100);
        await _navStore.RecordVisitAsync(200);

        var result = await _navStore.GoBackAsync();
        result.ShouldBe(100);
    }

    [Fact]
    public async Task GoBackAsync_MovesBackMultipleSteps()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);
        await _navStore.RecordVisitAsync(30);

        var step1 = await _navStore.GoBackAsync();
        step1.ShouldBe(20);

        var step2 = await _navStore.GoBackAsync();
        step2.ShouldBe(10);

        var step3 = await _navStore.GoBackAsync();
        step3.ShouldBeNull();
    }

    [Fact]
    public async Task GoBackAsync_UpdatesCursor()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);

        await _navStore.GoBackAsync();

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        cursorId.ShouldBe(entries[0].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GoForwardAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoForwardAsync_ReturnsNull_WhenEmpty()
    {
        var result = await _navStore.GoForwardAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GoForwardAsync_ReturnsNull_WhenAtHead()
    {
        await _navStore.RecordVisitAsync(42);
        var result = await _navStore.GoForwardAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GoForwardAsync_ReturnsNextWorkItemId_AfterGoBack()
    {
        await _navStore.RecordVisitAsync(100);
        await _navStore.RecordVisitAsync(200);

        await _navStore.GoBackAsync();
        var result = await _navStore.GoForwardAsync();
        result.ShouldBe(200);
    }

    [Fact]
    public async Task GoForwardAsync_MovesForwardMultipleSteps()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);
        await _navStore.RecordVisitAsync(30);

        // Go back to first entry
        await _navStore.GoBackAsync();
        await _navStore.GoBackAsync();

        var step1 = await _navStore.GoForwardAsync();
        step1.ShouldBe(20);

        var step2 = await _navStore.GoForwardAsync();
        step2.ShouldBe(30);

        var step3 = await _navStore.GoForwardAsync();
        step3.ShouldBeNull();
    }

    [Fact]
    public async Task GoForwardAsync_UpdatesCursor()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);

        await _navStore.GoBackAsync();
        await _navStore.GoForwardAsync();

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        cursorId.ShouldBe(entries[1].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Back/Forward interleaving
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BackAndForward_Interleaved()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);
        await _navStore.RecordVisitAsync(30);

        (await _navStore.GoBackAsync()).ShouldBe(20);
        (await _navStore.GoBackAsync()).ShouldBe(10);
        (await _navStore.GoForwardAsync()).ShouldBe(20);
        (await _navStore.GoForwardAsync()).ShouldBe(30);
        (await _navStore.GoForwardAsync()).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetHistoryAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNoEntries()
    {
        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(0);
        cursorId.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClearAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);

        await _navStore.ClearAsync();

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(0);
        cursorId.ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_WhenAlreadyEmpty_DoesNotThrow()
    {
        await _navStore.ClearAsync();

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(0);
        cursorId.ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_AllowsNewRecordsAfterClear()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.ClearAsync();
        await _navStore.RecordVisitAsync(99);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(1);
        entries[0].WorkItemId.ShouldBe(99);
        cursorId.ShouldBe(entries[0].Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Complex scenarios
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ForwardPruning_ThenCircularBuffer_Combined()
    {
        // Fill to 50 entries
        for (var i = 1; i <= 50; i++)
        {
            await _navStore.RecordVisitAsync(i);
        }

        // Go back 5 entries (cursor at 45)
        for (var i = 0; i < 5; i++)
        {
            await _navStore.GoBackAsync();
        }

        // Record a new entry — prunes 5 forward entries, inserts 1 → count = 46
        await _navStore.RecordVisitAsync(999);

        var (entries, _) = await _navStore.GetHistoryAsync();
        entries.Count.ShouldBe(46);
        entries[^1].WorkItemId.ShouldBe(999);
        // Items 46–50 should have been pruned
        entries[^2].WorkItemId.ShouldBe(45);
    }

    [Fact]
    public async Task RecordVisitAsync_AfterGoBack_CursorAtHead()
    {
        await _navStore.RecordVisitAsync(10);
        await _navStore.RecordVisitAsync(20);
        await _navStore.RecordVisitAsync(30);

        // Go back to entry 20
        await _navStore.GoBackAsync();

        // Record new visit — entry 30 is pruned, cursor should be at new entry
        await _navStore.RecordVisitAsync(40);

        var (entries, cursorId) = await _navStore.GetHistoryAsync();

        // Cursor should be at the last (newest) entry
        var maxId = entries.Max(e => e.Id);
        cursorId.ShouldBe(maxId);
    }
}
