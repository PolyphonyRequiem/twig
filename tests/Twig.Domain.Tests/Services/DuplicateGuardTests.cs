using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class DuplicateGuardTests
{
    private readonly IAdoWorkItemService _adoService;

    public DuplicateGuardTests()
    {
        _adoService = Substitute.For<IAdoWorkItemService>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No match — returns null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_NoMatch_ReturnsNull()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 10, title: "My Task", type: WorkItemType.Task);

        result.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Match found — returns fetched item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_MatchFound_ReturnsFetchedItem()
    {
        var existingItem = new WorkItemBuilder(42, "My Task").AsTask().WithParent(10).Build();

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 42 });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(existingItem);

        var result = await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 10, title: "My Task", type: WorkItemType.Task);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(42);
        result.Title.ShouldBe("My Task");
    }

    // ═══════════════════════════════════════════════════════════════
    //  WIQL query shape
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_BuildsWiqlWithParentTitleAndType()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 5, title: "My Issue", type: WorkItemType.Issue);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(q =>
                q.Contains("[System.Parent] = 5") &&
                q.Contains("[System.Title] = 'My Issue'") &&
                q.Contains($"[System.WorkItemType] = '{WorkItemType.Issue.Value}'")),
            Arg.Is<int>(1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindExistingChildAsync_PassesTopOne()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "T", type: WorkItemType.Task);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Is<int>(1), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Special characters in title — single-quote escaping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_TitleWithSingleQuote_EscapesCorrectly()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "O'Brien's Task", type: WorkItemType.Task);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(q => q.Contains("'O''Brien''s Task'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindExistingChildAsync_TitleWithMultipleSingleQuotes_AllEscaped()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "It's a 'test' title", type: WorkItemType.Task);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(q => q.Contains("'It''s a ''test'' title'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  CancellationToken forwarding
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "T", type: WorkItemType.Task, ct: token);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<int>(), token);
    }

    [Fact]
    public async Task FindExistingChildAsync_MatchFound_ForwardsCancellationTokenToFetch()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var item = new WorkItemBuilder(99, "T").AsTask().Build();
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 99 });
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "T", type: WorkItemType.Task, ct: token);

        await _adoService.Received(1).FetchAsync(99, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Does not call FetchAsync when no match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindExistingChildAsync_NoMatch_DoesNotCallFetch()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        await DuplicateGuard.FindExistingChildAsync(
            _adoService, parentId: 1, title: "T", type: WorkItemType.Task);

        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
