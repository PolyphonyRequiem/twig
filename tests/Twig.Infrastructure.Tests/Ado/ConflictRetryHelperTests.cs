using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Infrastructure.Ado;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class ConflictRetryHelperTests
{
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();

    private static readonly IReadOnlyList<FieldChange> Changes =
    [
        new FieldChange("System.Title", "Old", "New"),
    ];

    [Fact]
    public async Task FirstAttemptSucceeds_ReturnsNewRevision()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var result = await ConflictRetryHelper.PatchWithRetryAsync(
            _adoService, 42, Changes, 5, CancellationToken.None);

        result.ShouldBe(6);
        await _adoService.Received(1).PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstAttemptConflicts_RetrySucceeds_ReturnsNewRevision()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        var freshItem = new WorkItemBuilder(42, "Item").Build();
        freshItem.MarkSynced(7);
        _adoService
            .FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(freshItem);

        _adoService
            .PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>())
            .Returns(8);

        var result = await ConflictRetryHelper.PatchWithRetryAsync(
            _adoService, 42, Changes, 5, CancellationToken.None);

        result.ShouldBe(8);
        await _adoService.Received(2).PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BothAttemptsConflict_ThrowsAdoConflictException()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        var freshItem = new WorkItemBuilder(42, "Item").Build();
        freshItem.MarkSynced(7);
        _adoService
            .FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(freshItem);

        _adoService
            .PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(9));

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                _adoService, 42, Changes, 5, CancellationToken.None));

        ex.ServerRevision.ShouldBe(9);
        await _adoService.Received(2).PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstAttemptThrowsNonConflict_RethrowsImmediately()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                _adoService, 42, Changes, 5, CancellationToken.None));

        await _adoService.Received(1).PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationToken_IsPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        _adoService
            .PatchAsync(42, Changes, 5, ct)
            .Returns(6);

        await ConflictRetryHelper.PatchWithRetryAsync(
            _adoService, 42, Changes, 5, ct);

        await _adoService.Received(1).PatchAsync(42, Changes, 5, ct);
    }

    [Fact]
    public async Task FetchAsyncThrowsDuringRetry_PropagatesException()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        _adoService
            .FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        await Should.ThrowAsync<HttpRequestException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                _adoService, 42, Changes, 5, CancellationToken.None));

        await _adoService.Received(1).PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>());
    }
}
