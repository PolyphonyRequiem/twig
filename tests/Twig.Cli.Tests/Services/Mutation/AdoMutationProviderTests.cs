using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Services.Mutation;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Services.Mutation;

public sealed class AdoMutationProviderTests
{
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly AdoMutationProvider _sut;

    public AdoMutationProviderTests()
    {
        _sut = new AdoMutationProvider(_adoService, _workItemRepo, _pendingChangeStore);
        // Default: no pending changes
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  UpdateFieldAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateField_CallsPatchWithRetry()
    {
        var remote = BuildRemote(42, revision: 5);
        SetupFetchSequence(42, remote, BuildRemote(42, revision: 6));
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var change = new FieldChange("System.Title", "Old", "New");
        var result = await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldBe(6);
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 1 && c[0].FieldName == "System.Title" && c[0].NewValue == "New"),
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeState_CallsPatchWithRetry()
    {
        var remote = BuildRemote(42, revision: 5);
        SetupFetchSequence(42, remote, BuildRemote(42, revision: 6));
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var stateChange = new FieldChange("System.State", "New", "Active");
        var result = await _sut.ChangeStateAsync(42, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldBe(6);
        await _adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Count == 1 && c[0].FieldName == "System.State" && c[0].NewValue == "Active"),
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateField_ConflictRetry_Succeeds()
    {
        var remote = BuildRemote(42, revision: 5);
        var refreshed = BuildRemote(42, revision: 7);
        var final = BuildRemote(42, revision: 8);

        // First fetch returns remote, then refreshed (after conflict retry re-fetch), then final (post-mutation resync)
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(remote, refreshed, final);

        // First patch throws conflict, second (retry with refreshed revision) succeeds
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(6));
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 7, Arg.Any<CancellationToken>())
            .Returns(8);

        var change = new FieldChange("System.Title", "Old", "New");
        var result = await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldBe(8);
    }

    [Fact]
    public async Task UpdateField_ConflictExhausted_ReturnsError()
    {
        var remote = BuildRemote(42, revision: 5);
        var refreshed = BuildRemote(42, revision: 7);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(remote, refreshed);

        // Both patches throw conflict
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(99));

        var change = new FieldChange("System.Title", "Old", "New");
        var result = await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("conflict");
    }

    [Fact]
    public async Task UpdateField_RefreshesCache()
    {
        var remote = BuildRemote(42, revision: 5);
        var updated = BuildRemote(42, revision: 6);
        SetupFetchSequence(42, remote, updated);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var change = new FieldChange("Custom.Field", null, "value");
        await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        // Fetch called twice: once before patch (for revision), once after (for cache resync)
        await _adoService.Received(2).FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoPushNotes_BestEffort()
    {
        var remote = BuildRemote(42, revision: 5);
        var updated = BuildRemote(42, revision: 6);
        SetupFetchSequence(42, remote, updated);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        // Make notes helper fail by having GetChangesAsync throw
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var change = new FieldChange("System.Title", "Old", "New");
        var result = await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        // Mutation should still succeed despite notes failure
        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldBe(6);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ChangeStateAsync — additional coverage
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChangeState_RefreshesCache()
    {
        var remote = BuildRemote(10, revision: 3);
        var updated = BuildRemote(10, revision: 4);
        SetupFetchSequence(10, remote, updated);
        _adoService.PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);

        var stateChange = new FieldChange("System.State", "New", "Closed");
        await _sut.ChangeStateAsync(10, stateChange, CancellationToken.None);

        await _adoService.Received(2).FetchAsync(10, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeState_ConflictExhausted_ReturnsError()
    {
        var remote = BuildRemote(10, revision: 3);
        var refreshed = BuildRemote(10, revision: 5);

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .Returns(remote, refreshed);

        _adoService.PatchAsync(10, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(99));

        var stateChange = new FieldChange("System.State", "New", "Active");
        var result = await _sut.ChangeStateAsync(10, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("conflict");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem BuildRemote(int id, int revision = 1)
    {
        var item = new WorkItemBuilder(id, $"Item {id}").Build();
        item.MarkSynced(revision);
        return item;
    }

    /// <summary>
    /// Configures FetchAsync to return items in sequence: first call returns
    /// <paramref name="first"/>, subsequent calls return <paramref name="then"/>.
    /// </summary>
    private void SetupFetchSequence(int itemId, WorkItem first, WorkItem then)
    {
        _adoService.FetchAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(first, then);
    }
}
