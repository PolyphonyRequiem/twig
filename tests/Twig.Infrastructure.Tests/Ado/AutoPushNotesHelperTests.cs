using NSubstitute;
using Twig.Infrastructure.Ado;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Tests for <see cref="AutoPushNotesHelper"/> covering all branches:
/// empty changes, notes with null NewValue, notes with non-null NewValue,
/// mixed note/field changes, and the hasNotes guard for ClearChangesByTypeAsync.
/// </summary>
public class AutoPushNotesHelperTests
{
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;

    public AutoPushNotesHelperTests()
    {
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
    }

    [Fact]
    public async Task NoPendingChanges_DoesNothing()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.DidNotReceive().AddCommentAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteWithNullNewValue_IsSkipped()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "note", null, null, null) });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.DidNotReceive().AddCommentAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteWithNonNullNewValue_IsPushedAndCleared()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "note", null, null, "My note text") });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.Received(1).AddCommentAsync(42, "My note text", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleNotes_AllPushedThenCleared()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(42, "note", null, null, "Note 1"),
                new PendingChangeRecord(42, "note", null, null, "Note 2"),
            });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.Received(1).AddCommentAsync(42, "Note 1", Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddCommentAsync(42, "Note 2", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixedNoteAndFieldChanges_OnlyNotesPushed()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(42, "field", "System.Title", "Old", "New"),
                new PendingChangeRecord(42, "note", null, null, "A note"),
                new PendingChangeRecord(42, "field", "System.State", "New", "Active"),
            });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.Received(1).AddCommentAsync(42, "A note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllNotesHaveNullNewValue_NoClearCalled()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(42, "note", null, null, null),
                new PendingChangeRecord(42, "note", null, null, null),
            });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.DidNotReceive().AddCommentAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeTypeCaseInsensitive_NoteWithUpperCase_IsPushed()
    {
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "Note", null, null, "Upper case note") });

        await AutoPushNotesHelper.PushAndClearAsync(42, _pendingChangeStore, _adoService);

        await _adoService.Received(1).AddCommentAsync(42, "Upper case note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
    }
}
