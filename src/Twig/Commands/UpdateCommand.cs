using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig update &lt;field&gt; &lt;value&gt;</c>: pulls latest from ADO,
/// conflict-resolves, applies change, pushes, auto-pushes notes, clears pending, updates cache.
/// Routes through <see cref="SeedMutationProvider"/> for local-only seeds.
/// </summary>
public sealed class UpdateCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    IFieldDefinitionStore fieldDefStore,
    OutputFormatterFactory formatterFactory,
    SeedMutationProvider seedMutationProvider,
    IPromptStateWriter? promptStateWriter = null,
    TextReader? stdinReader = null,
    TextWriter? stderr = null,
    TextWriter? stdout = null)
{
    private readonly TextReader _stdin = stdinReader ?? Console.In;
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TextWriter _stdout = stdout ?? Console.Out;

    public async Task<int> ExecuteAsync(string field, string? value = null, string outputFormat = OutputFormatterFactory.DefaultFormat, string? format = null, string? filePath = null, bool readStdin = false, int? id = null, bool append = false, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(field))
        {
            _stderr.WriteLine(fmt.FormatError("Usage: twig update <field> <value>"));
            return 2;
        }

        // Value source validation: exactly one of inline value, --file, or --stdin must be specified.
        var sourceCount = (value is not null ? 1 : 0) + (filePath is not null ? 1 : 0) + (readStdin ? 1 : 0);
        if (sourceCount == 0)
        {
            _stderr.WriteLine(fmt.FormatError("No value specified. Provide inline value, --file <path>, or --stdin."));
            return 2;
        }
        if (sourceCount > 1)
        {
            _stderr.WriteLine(fmt.FormatError("Multiple value sources. Use exactly one of: inline value, --file, or --stdin."));
            return 2;
        }

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
        {
            _stderr.WriteLine(fmt.FormatError(formatError));
            return 2;
        }

        // Resolve the effective value from the selected source.
        string resolvedValue;
        if (filePath is not null)
        {
            if (!File.Exists(filePath))
            {
                _stderr.WriteLine(fmt.FormatError($"File not found: {filePath}"));
                return 2;
            }
            resolvedValue = await File.ReadAllTextAsync(filePath, ct);
        }
        else if (readStdin)
        {
            resolvedValue = await _stdin.ReadToEndAsync(ct);
        }
        else
        {
            resolvedValue = value!;
        }
        if (format is null && (filePath is not null || readStdin))
            resolvedValue = resolvedValue.TrimEnd('\r', '\n');

        var resolution = await HtmlFieldFormatter.ResolveAsync(
            field, resolvedValue, format, fieldDefStore,
            onMissingFieldDef: name =>
                _stderr.WriteLine($"warning: field type unknown for '{name}'; not converting. Use --format markdown to force conversion."),
            ct);
        var effectiveValue = resolution.EffectiveValue;

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var local, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        // Seed routing: local-only mutation, no ADO interaction.
        if (local.IsSeed)
        {
            if (append)
            {
                local.Fields.TryGetValue(field, out var existingValue);
                effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: resolution.IsHtml);
            }

            var change = new FieldChange(field, null, effectiveValue);
            var result = await seedMutationProvider.UpdateFieldAsync(local.Id, change, ct);
            if (!result.IsSuccess)
            {
                _stderr.WriteLine(fmt.FormatError(result.ErrorMessage ?? "Failed to update field."));
                return 1;
            }

            if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

            var displayValue = filePath is not null ? $"[from file: {filePath}]"
                             : readStdin ? "[from stdin]"
                             : resolvedValue;
            _stdout.WriteLine(fmt.FormatSuccess($"#{local.Id} {local.Title} updated: {field} = '{displayValue}'"));
            return 0;
        }

        // ── Published (ADO) flow ────────────────────────────────────────

        var remote = await adoService.FetchAsync(local.Id);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            local, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{local.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return 1;
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return 0;

        if (append)
        {
            remote.Fields.TryGetValue(field, out var existingValue);
            effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: resolution.IsHtml);
        }

        var changes = new[] { new FieldChange(field, null, effectiveValue) };
        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, local.Id, changes, remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            _stderr.WriteLine(fmt.FormatError("Concurrency conflict after retry. Run 'twig sync' and retry."));
            return 1;
        }

        await AutoPushNotesHelper.PushAndClearAsync(local.Id, pendingChangeStore, adoService);

        var updated = await adoService.FetchAsync(local.Id);
        await workItemRepo.SaveAsync(updated);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var displayValue2 = filePath is not null ? $"[from file: {filePath}]"
                         : readStdin ? "[from stdin]"
                         : resolvedValue;
        _stdout.WriteLine(fmt.FormatSuccess($"#{local.Id} {local.Title} updated: {field} = '{displayValue2}'"));

        return 0;
    }

}