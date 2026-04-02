using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for FR-7 / G-6: <c>twig save --all</c> continues past per-item failures
/// instead of terminating the loop. Verifies that unhandled exceptions from ADO calls
/// are caught, logged to stderr, and remaining dirty items are still attempted.
/// </summary>
public sealed class SaveCommandContinueOnFailureTests : SaveCommandTestBase
{
    public SaveCommandContinueOnFailureTests() { }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllFlag_NoFailures_ReturnsZero()
    {
        // Sanity: when nothing fails, return code is still 0.
        var item = CreateWorkItem(1, "OK");
        var remote = CreateWorkItem(1, "OK");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task SingleItem_ExceptionStillCaught_ReturnsError()
    {
        // Even single-item saves benefit from try-catch (no loop to continue, but error is handled gracefully).
        var item = CreateWorkItem(42, "Single");

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "field", "System.Title", "Old", "New") });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Auth expired"));

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#42");
        stderr.ToString().ShouldContain("Auth expired");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
