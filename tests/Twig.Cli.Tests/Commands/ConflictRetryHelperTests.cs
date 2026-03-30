using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ConflictRetryHelperTests
{
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();

    private static readonly IReadOnlyList<FieldChange> Changes =
    [
        new FieldChange("System.Title", "Old", "New"),
    ];

    // ── Happy path ──────────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptSucceeds_ReturnsNewRevision()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var result = await ConflictRetryHelper.PatchWithRetryAsync(
            _adoService, 42, Changes, 5, CancellationToken.None);

        result.ShouldBe(6);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Retry succeeds ──────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptConflicts_RetrySucceeds_ReturnsNewRevision()
    {
        // First call → conflict
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        // Re-fetch returns fresh item at revision 7
        var freshItem = new WorkItemBuilder(42, "Item").Build();
        freshItem.MarkSynced(7);
        _adoService
            .FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(freshItem);

        // Retry with fresh revision succeeds
        _adoService
            .PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>())
            .Returns(8);

        var result = await ConflictRetryHelper.PatchWithRetryAsync(
            _adoService, 42, Changes, 5, CancellationToken.None);

        result.ShouldBe(8);
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
    }

    // ── Retry also conflicts (genuine concurrent edit) ──────────

    [Fact]
    public async Task BothAttemptsConflict_ThrowsAdoConflictException()
    {
        // First call → conflict
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        // Re-fetch
        var freshItem = new WorkItemBuilder(42, "Item").Build();
        freshItem.MarkSynced(7);
        _adoService
            .FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(freshItem);

        // Retry also conflicts
        _adoService
            .PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(9));

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                _adoService, 42, Changes, 5, CancellationToken.None));

        ex.ServerRevision.ShouldBe(9);
    }

    // ── Non-conflict exception on first attempt → no retry ──────

    [Fact]
    public async Task FirstAttemptThrowsNonConflict_RethrowsImmediately()
    {
        _adoService
            .PatchAsync(42, Changes, 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                _adoService, 42, Changes, 5, CancellationToken.None));

        // Should NOT have attempted a re-fetch
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Cancellation is respected ───────────────────────────────

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
}
