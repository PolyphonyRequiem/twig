using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="NavigationTools.Query"/> (twig_query MCP tool).
/// Covers filter combinations, ADO delegation, truncation, caching, and error paths.
/// </summary>
public sealed class NavigationToolsQueryTests : NavigationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  No results — returns empty items array
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_NoResults_ReturnsEmptyItems()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(type: "Bug");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("items").GetArrayLength().ShouldBe(0);
        json.GetProperty("totalCount").GetInt32().ShouldBe(0);
        json.GetProperty("isTruncated").GetBoolean().ShouldBe(false);

        await _adoService.DidNotReceive().FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Results returned — items batched, cached, formatted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_WithResults_FetchesBatchAndCaches()
    {
        var item1 = new WorkItemBuilder(10, "Bug A").AsBug().InState("Active").Build();
        var item2 = new WorkItemBuilder(11, "Bug B").AsBug().InState("Active").Build();

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 11 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItem[] { item1, item2 });

        var result = await CreateSut().Query(type: "Bug");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("items").GetArrayLength().ShouldBe(2);
        json.GetProperty("totalCount").GetInt32().ShouldBe(2);

        await _workItemRepo.Received(1).SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Truncation — items.Count == top signals isTruncated = true
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_ResultCountEqualsTop_IsTruncatedTrue()
    {
        var items = Enumerable.Range(1, 5)
            .Select(i => new WorkItemBuilder(i, $"Item {i}").AsTask().Build())
            .ToArray();

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), 5, Arg.Any<CancellationToken>())
            .Returns(items.Select(x => x.Id).ToArray());
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await CreateSut().Query(top: 5);

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("isTruncated").GetBoolean().ShouldBe(true);
    }

    [Fact]
    public async Task Query_ResultCountLessThanTop_IsTruncatedFalse()
    {
        var items = Enumerable.Range(1, 3)
            .Select(i => new WorkItemBuilder(i, $"Item {i}").AsTask().Build())
            .ToArray();

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), 10, Arg.Any<CancellationToken>())
            .Returns(items.Select(x => x.Id).ToArray());
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await CreateSut().Query(top: 10);

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("isTruncated").GetBoolean().ShouldBe(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Query description reflects supplied filters
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_WithTypeAndStateFilter_QueryDescriptionReflectsFilters()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(type: "Bug", state: "Active");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        var desc = json.GetProperty("queryDescription").GetString()!;
        desc.ShouldContain("Bug");
        desc.ShouldContain("Active");
    }

    [Fact]
    public async Task Query_NoFilters_QueryDescriptionIsAllItems()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query();

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("queryDescription").GetString().ShouldBe("all items");
    }

    // ═══════════════════════════════════════════════════════════════
    //  WIQL query is passed to ADO with the correct top
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_TopParameter_IsPassedToAdoService()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), 7, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await CreateSut().Query(top: 7);

        await _adoService.Received(1).QueryByWiqlAsync(Arg.Any<string>(), 7, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO query failure → error result
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_AdoQueryFails_ReturnsError()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unavailable"));

        var result = await CreateSut().Query(type: "Bug");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Query failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache save failure is best-effort (no error returned)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_CacheSaveFails_StillReturnsItems()
    {
        var items = new[] { new WorkItemBuilder(5, "Task").AsTask().Build() };

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 5 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _workItemRepo.SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var result = await CreateSut().Query();

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  OperationCanceledException propagates through ADO call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_Cancelled_PropagatesException()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateSut().Query(type: "Bug"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response includes workspace key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Query_Response_IncludesWorkspaceKey()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query();

        result.IsError.ShouldBeNull();
        ParseResult(result).GetProperty("workspace").GetString().ShouldBe(TestWorkspaceKey.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    //  createdSince / changedSince wired into WIQL and description
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    public async Task Query_CreatedSince_IncludedInWiqlAndDescription(int days)
    {
        string? capturedWiql = null;
        _adoService.QueryByWiqlAsync(Arg.Do<string>(w => capturedWiql = w), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(createdSince: days);

        result.IsError.ShouldBeNull();
        capturedWiql.ShouldNotBeNull();
        capturedWiql.ShouldContain($"[System.CreatedDate] >= @Today - {days}");
        var desc = ParseResult(result).GetProperty("queryDescription").GetString()!;
        desc.ShouldContain($"created within {days}d");
    }

    [Fact]
    public async Task Query_ChangedSince_IncludedInWiqlAndDescription()
    {
        string? capturedWiql = null;
        _adoService.QueryByWiqlAsync(Arg.Do<string>(w => capturedWiql = w), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(changedSince: 14);

        result.IsError.ShouldBeNull();
        capturedWiql.ShouldNotBeNull();
        capturedWiql.ShouldContain("[System.ChangedDate] >= @Today - 14");
        var desc = ParseResult(result).GetProperty("queryDescription").GetString()!;
        desc.ShouldContain("changed within 14d");
    }

    [Fact]
    public async Task Query_DateFilterWithSearchText_ProducesCorrectWiqlAndDescription()
    {
        string? capturedWiql = null;
        _adoService.QueryByWiqlAsync(Arg.Do<string>(w => capturedWiql = w), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(searchText: "login bug", createdSince: 5);

        result.IsError.ShouldBeNull();
        capturedWiql.ShouldNotBeNull();
        capturedWiql.ShouldContain("CONTAINS 'login bug'");
        capturedWiql.ShouldContain("[System.CreatedDate] >= @Today - 5");
        var desc = ParseResult(result).GetProperty("queryDescription").GetString()!;
        desc.ShouldContain("title or description contains 'login bug'");
        desc.ShouldContain("created within 5d");
    }

    [Fact]
    public async Task Query_DateFilterWithTypeFilter_DescriptionContainsAndSeparator()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(type: "Bug", changedSince: 3);

        result.IsError.ShouldBeNull();
        var desc = ParseResult(result).GetProperty("queryDescription").GetString()!;
        desc.ShouldContain("type = 'Bug'");
        desc.ShouldContain("changed within 3d");
        desc.ShouldContain(" AND ");
    }

    [Fact]
    public async Task Query_NullDateFilters_NotIncludedInWiql()
    {
        string? capturedWiql = null;
        _adoService.QueryByWiqlAsync(Arg.Do<string>(w => capturedWiql = w), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateSut().Query(type: "Bug");

        result.IsError.ShouldBeNull();
        capturedWiql.ShouldNotBeNull();
        // The ORDER BY clause uses System.ChangedDate, so check that no date *filter* clause exists
        capturedWiql.ShouldNotContain("System.CreatedDate");
        capturedWiql.ShouldNotContain("@Today");
    }
}
