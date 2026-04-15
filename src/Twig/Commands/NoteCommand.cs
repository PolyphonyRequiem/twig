using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig note ["text"]</c>: adds a note (ADO comment) to the active work item.
/// If text is provided inline it is pushed immediately; otherwise an editor is launched.
/// </summary>
/// <remarks>
/// <para><strong>Code paths</strong></para>
/// <list type="number">
///   <item>
///     <term>Push-on-write (non-seed, online)</term>
///     <description>
///       Calls <see cref="IAdoWorkItemService.AddCommentAsync"/> directly, then clears any
///       locally-staged notes via <see cref="IPendingChangeStore.ClearChangesByTypeAsync"/>.
///       An inner try-catch re-fetches the work item to resync the local cache; resync failure
///       is non-fatal — the note is already in ADO.
///     </description>
///   </item>
///   <item>
///     <term>Offline fallback (non-seed, ADO unreachable)</term>
///     <description>
///       When <c>AddCommentAsync</c> throws, the note is staged locally via
///       <see cref="StageLocallyAsync"/> and will be flushed later by
///       <see cref="PendingChangeFlusher"/> (save/sync/flow-done) or by
///       <see cref="Twig.Infrastructure.Ado.AutoPushNotesHelper"/> (update/state/edit).
///     </description>
///   </item>
///   <item>
///     <term>Seed staging</term>
///     <description>
///       Seed items have no ADO identity, so notes are always staged locally via
///       <see cref="StageLocallyAsync"/>. They are flushed when the seed is published
///       through <see cref="PendingChangeFlusher"/>.
///     </description>
///   </item>
/// </list>
/// <para><strong>Related components</strong></para>
/// <list type="bullet">
///   <item>
///     <see cref="Twig.Infrastructure.Ado.AutoPushNotesHelper"/> — side-effect flusher that
///     pushes residual staged notes during <c>update</c>, <c>state</c>, and <c>edit</c> commands.
///   </item>
///   <item>
///     <see cref="PendingChangeFlusher"/> — batch flusher used by <c>save</c>, <c>sync</c>,
///     and <c>flow-done</c> for any remaining staged notes (offline fallback or seed publish).
///   </item>
/// </list>
/// </remarks>
public sealed class NoteCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IAdoWorkItemService adoService,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    private readonly IAdoWorkItemService _adoService = adoService;

    /// <summary>Add a note/comment to the active work item.</summary>
    public async Task<int> ExecuteAsync(string? text = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        string? noteText = text;

        if (string.IsNullOrWhiteSpace(noteText))
        {
            noteText = await editorLauncher.LaunchAsync(
                $"# Note for #{item.Id} {item.Title}\n# Lines starting with # are comments and will be removed.\n\n");

            if (noteText is null)
            {
                Console.WriteLine(fmt.FormatInfo("Note cancelled (empty or unchanged)."));
                return 0;
            }

            var lines = noteText.Split('\n')
                .Where(l => !l.TrimStart().StartsWith('#'))
                .ToArray();
            noteText = string.Join('\n', lines).Trim();

            if (string.IsNullOrWhiteSpace(noteText))
            {
                Console.WriteLine(fmt.FormatInfo("Note cancelled (empty after stripping comments)."));
                return 0;
            }
        }

        string successMessage;

        if (item.IsSeed)
        {
            await StageLocallyAsync(item, noteText, ct);
            successMessage = fmt.FormatSuccess($"Note added to #{item.Id} (pending).");
        }
        else
        {
            try
            {
                await _adoService.AddCommentAsync(item.Id, noteText, ct);
                await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);

                try
                {
                    var updated = await _adoService.FetchAsync(item.Id, ct);
                    await workItemRepo.SaveAsync(updated, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Note pushed but cache may be stale — run 'twig sync' to resync ({ex.Message})");
                }

                successMessage = fmt.FormatSuccess($"Note added to #{item.Id}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Note staged locally (ADO unreachable): {ex.Message}");
                await StageLocallyAsync(item, noteText, ct);
                successMessage = fmt.FormatSuccess($"Note added to #{item.Id} (pending).");
            }
        }

        Console.WriteLine(successMessage);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var hints = hintEngine.GetHints("note", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    private async Task StageLocallyAsync(Twig.Domain.Aggregates.WorkItem item, string noteText, CancellationToken ct)
    {
        await pendingChangeStore.AddChangeAsync(
            item.Id,
            "note",
            fieldName: null,
            oldValue: null,
            newValue: noteText);

        item.AddNote(new PendingNote(noteText, DateTimeOffset.UtcNow, IsHtml: false));
        item.ApplyCommands();
        await workItemRepo.SaveAsync(item, ct);
    }
}
