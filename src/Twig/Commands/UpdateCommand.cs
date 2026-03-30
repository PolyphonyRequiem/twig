using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Content;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig update &lt;field&gt; &lt;value&gt;</c>: pulls latest from ADO,
/// conflict-resolves, applies change, pushes, auto-pushes notes, clears pending, updates cache.
/// </summary>
public sealed class UpdateCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stderr = null,
    TextWriter? stdout = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TextWriter _stdout = stdout ?? Console.Out;

    /// <summary>Update a field on the active work item and push to ADO.</summary>
    public async Task<int> ExecuteAsync(string field, string value, string outputFormat = OutputFormatterFactory.DefaultFormat, string? format = null, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(field))
        {
            _stderr.WriteLine(fmt.FormatError("Usage: twig update <field> <value>"));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var local, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        var remote = await adoService.FetchAsync(local.Id);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            local, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{local.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            _stderr.WriteLine(fmt.FormatError($"Unknown format '{format}'. Supported formats: markdown"));
            return 2;
        }
        var effectiveValue = format is null ? value : MarkdownConverter.ToHtml(value);

        var changes = new[] { new FieldChange(field, null, effectiveValue) };
        await ConflictRetryHelper.PatchWithRetryAsync(adoService, local.Id, changes, remote.Revision, ct);

        await AutoPushNotesHelper.PushAndClearAsync(local.Id, pendingChangeStore, adoService);

        var updated = await adoService.FetchAsync(local.Id);
        await workItemRepo.SaveAsync(updated);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        _stdout.WriteLine(fmt.FormatSuccess($"#{local.Id} updated: {field} = '{value}'"));

        return 0;
    }

}
