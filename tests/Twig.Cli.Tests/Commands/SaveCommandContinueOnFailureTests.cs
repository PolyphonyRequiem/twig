using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
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
    //  Core: exception during save does not terminate the loop
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllFlag_FetchThrowsForFirstItem_SecondItemStillSaved()
    {
        // Item 1 throws on FetchAsync (conflict resolution path); item 2 should still succeed.
        var item1 = CreateWorkItem(1, "Failing");
        var item2 = CreateWorkItem(2, "Succeeding");
        var remote2 = CreateWorkItem(2, "Succeeding");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        // Item 1: FetchAsync throws
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unavailable"));
        // Item 2: normal path
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1); // hadErrors = true
        stderr.ToString().ShouldContain("#1");
        stderr.ToString().ShouldContain("ADO unavailable");
        // Item 2 was still attempted and saved
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllFlag_PatchThrowsForFirstItem_SecondItemStillSaved()
    {
        // Item 1 throws on PatchAsync; item 2 should still succeed.
        var item1 = CreateWorkItem(1, "Failing");
        var item2 = CreateWorkItem(2, "Succeeding");
        var remote1 = CreateWorkItem(1, "Failing");
        var remote2 = CreateWorkItem(2, "Succeeding");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        // Item 1: PatchAsync throws
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("409 Conflict"));
        // Item 2: normal
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#1");
        stderr.ToString().ShouldContain("409 Conflict");
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllFlag_AddCommentThrowsForNotesOnlyItem_SecondItemStillSaved()
    {
        // Item 1 (notes-only) throws on AddCommentAsync; item 2 should still succeed.
        var item1 = CreateWorkItem(1, "Notes Fail");
        var item2 = CreateWorkItem(2, "Succeeding");
        var remote2 = CreateWorkItem(2, "Succeeding");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.AddCommentAsync(1, "A note", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#1");
        stderr.ToString().ShouldContain("500 Internal Server Error");
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllFlag_ResyncFetchThrows_ErrorLoggedAndContinues()
    {
        // Item 1: post-push FetchAsync (cache resync) throws; item 2 still saved.
        var item1 = CreateWorkItem(1, "Resync Fail");
        var item2 = CreateWorkItem(2, "OK");
        var remote1 = CreateWorkItem(1, "Resync Fail");
        var remote2 = CreateWorkItem(2, "OK");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        // Item 1: AddCommentAsync succeeds but post-push FetchAsync throws
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network timeout"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#1");
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllFlag_AllItemsFail_ReturnsError()
    {
        var item1 = CreateWorkItem(1, "Fail");
        var item2 = CreateWorkItem(2, "Also Fail");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Fail 1"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Fail 2"));

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        var errors = stderr.ToString();
        errors.ShouldContain("#1");
        errors.ShouldContain("Fail 1");
        errors.ShouldContain("#2");
        errors.ShouldContain("Fail 2");
    }

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
