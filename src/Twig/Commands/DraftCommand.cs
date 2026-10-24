using System.Diagnostics;
using System.Globalization;
using Twig.Browser;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

/// <summary>Stages and inspects recoverable, local-only work-item drafts.</summary>
internal sealed class DraftCommand(
    CommandContext ctx,
    BrowserReadModule browser,
    BrowserEditingModule editing,
    BrowserOperationModule operations,
    IRecoverableWorkItemDraftStore drafts,
    RendererFactory renderers,
    TextWriter? stdout = null)
{
    internal async Task<int> SetAsync(
        int id,
        string fieldRef,
        string value,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exitCode = 1;
        try
        {
            var format = ValidateFormat(outputFormat);
            if (string.IsNullOrWhiteSpace(fieldRef))
                throw new DraftCommandException(2, "A field reference is required.");

            var opened = await OpenAsync(id, ct);
            var session = opened.Session;
            var key = opened.Document.ItemKey;
            if (session.HasLocalChanges)
                throw new DraftCommandException(1,
                    "Local pending work exists for this item. Reconcile it before staging a separate recoverable draft.");

            var existing = await drafts.GetAsync(key, ct);
            var draft = existing ?? RecoverableWorkItemDraft.Create(key, session.Revision);
            if (existing is not null)
            {
                var hadConflicts = existing.HasConflicts;
                var observed = Observe(draft, session);
                if (observed.Generation != draft.Generation)
                {
                    await SaveUnderAdmissionAsync(session.Capture, observed, draft.Generation, ct);
                    draft = observed;
                }

                if (hadConflicts || draft.HasConflicts || session.Revision != draft.BaselineRevision)
                    throw new DraftCommandException(1,
                        "The cached revision or changed fields no longer match this draft's baseline. Intent was retained; reconcile before editing again.");
            }

            if (!session.CurrentValues.TryGetValue(fieldRef, out var baselineValue))
                throw new DraftCommandException(1,
                    $"Field '{fieldRef}' is absent from the native form or its value is not carried by Twig.");
            var canonicalField = session.CanonicalField(fieldRef);
            if (!session.Capability.CanEdit(canonicalField))
                throw new DraftCommandException(1,
                    $"Field '{canonicalField}' is not persistable by the native edit capability.");

            var unchanged = draft.Changes.FirstOrDefault(change =>
                string.Equals(change.FieldRef, canonicalField, StringComparison.OrdinalIgnoreCase));
            if (unchanged is not null && string.Equals(unchanged.ProposedValue, value, StringComparison.Ordinal))
            {
                RenderDraft(draft, session.Revision, "draft set", format);
                return exitCode = 0;
            }
            if (unchanged is null && string.Equals(baselineValue, value, StringComparison.Ordinal))
            {
                if (existing is null)
                    await SaveUnderAdmissionAsync(session.Capture, draft, null, ct);
                RenderDraft(draft, session.Revision, "draft set", format);
                return exitCode = 0;
            }

            var updated = draft.SetField(canonicalField, baselineValue, value);
            ValidateDraft(updated, session.Capability);
            await SaveUnderAdmissionAsync(session.Capture, updated, existing?.Generation, ct);
            RenderDraft(updated, session.Revision, "draft set", format);
            return exitCode = 0;
        }
        catch (DraftCommandException ex)
        {
            exitCode = ex.ExitCode;
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        catch (ArgumentException ex)
        {
            exitCode = 2;
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 1;
            WriteError(outputFormat, "Draft staging was canceled; no remote write was attempted.");
            return exitCode;
        }
        catch (Exception ex)
        {
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        finally
        {
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "draft set", TelemetryFormat(outputFormat),
                exitCode, started);
        }
    }

    internal async Task<int> ShowAsync(
        int id,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool document = false,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exitCode = 1;
        try
        {
            var format = ValidateFormat(outputFormat);
            if (document && format is not ("json" or "json-full" or "json-compact"))
                throw new ArgumentException("--document requires JSON output (-o json).");

            var opened = await OpenAsync(id, ct);
            var session = opened.Session;
            var draft = await drafts.GetAsync(opened.Document.ItemKey, ct)
                ?? throw new DraftCommandException(1, $"No recoverable draft exists for work item #{id}.");

            var observed = session.HasLocalChanges
                ? draft.Observe(session.Revision, draft.Conflicts)
                : Observe(draft, session);
            if (observed.Generation != draft.Generation)
            {
                await SaveUnderAdmissionAsync(session.Capture, observed, draft.Generation, ct);
                draft = observed;
            }

            RenderDraft(draft, session.Revision, "draft show", format, session.HasLocalChanges,
                session, document);
            return exitCode = 0;
        }
        catch (DraftCommandException ex)
        {
            exitCode = ex.ExitCode;
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        catch (ArgumentException ex)
        {
            exitCode = 2;
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = 1;
            WriteError(outputFormat, "Draft inspection was canceled.");
            return exitCode;
        }
        catch (Exception ex)
        {
            WriteError(outputFormat, ex.Message);
            return exitCode;
        }
        finally
        {
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "draft show", TelemetryFormat(outputFormat),
                exitCode, started);
        }
    }

    private async Task<OpenedDraftItem> OpenAsync(int id, CancellationToken ct)
    {
        var result = await browser.ReadDetailAsync(new(id, Ansi: false, Document: true), ct);
        if (result.Failure is { } failure)
            throw new DraftCommandException(failure.Kind == BrowserReadFailureKind.Usage ? 2 : 1, failure.Message);

        var observation = result.Observation
            ?? throw new InvalidOperationException("The native detail read returned no observation.");
        var document = observation.Document
            ?? throw new InvalidOperationException("The native detail read returned no semantic document.");
        if (document.ItemKey.WorkItemId != id)
            throw new InvalidOperationException("The native detail identity does not match the requested work item.");

        var opened = await editing.OpenAsync(id, observation.Capture, ct);
        if (opened.Failure is { } editFailure)
            throw new DraftCommandException(editFailure.Kind == BrowserReadFailureKind.Usage ? 2 : 1, editFailure.Message);
        var session = opened.Value
            ?? throw new InvalidOperationException("The native editor returned no session.");
        if (session.WorkItemId != id || session.Revision != observation.Revision)
            throw new DraftCommandException(1,
                "The item changed between native read and editor open. No draft was changed; read it again before retrying.");

        return new(session, document);
    }

    private async Task SaveUnderAdmissionAsync(
        BrowserCapture capture,
        RecoverableWorkItemDraft draft,
        long? expectedGeneration,
        CancellationToken ct)
    {
        var result = await operations.ExecuteAsync(capture,
            (_, token) => drafts.TrySaveAsync(draft, expectedGeneration, token), ct);
        if (result.Failure is { } failure)
            throw new DraftCommandException(1, failure.Message);
        if (result.Value != true)
            throw new DraftCommandException(1,
                "The recoverable draft changed in another surface. No retry was attempted; inspect it again before editing.");
    }

    private static RecoverableWorkItemDraft Observe(
        RecoverableWorkItemDraft draft,
        BrowserEditingSession session)
    {
        var conflicts = draft.Changes
            .Select(change => TryGetObservedValue(session.Snapshot, change.FieldRef, out var value)
                && !string.Equals(change.BaselineValue, value, StringComparison.Ordinal)
                    ? new DraftFieldConflict(change.FieldRef, change.BaselineValue, value, change.ProposedValue)
                    : null)
            .Where(conflict => conflict is not null)
            .Select(conflict => conflict!)
            .OrderBy(conflict => conflict.FieldRef, StringComparer.Ordinal)
            .ToArray();
        return draft.Observe(session.Revision, conflicts);
    }

    private static bool TryGetObservedValue(
        Twig.Domain.ValueObjects.WorkItemSnapshot snapshot,
        string fieldRef,
        out string? value)
    {
        if (fieldRef.Equals("System.Title", StringComparison.OrdinalIgnoreCase))
        {
            value = snapshot.Title;
            return true;
        }
        if (fieldRef.Equals("System.State", StringComparison.OrdinalIgnoreCase))
        {
            value = snapshot.State;
            return true;
        }
        if (fieldRef.Equals("System.AssignedTo", StringComparison.OrdinalIgnoreCase))
        {
            value = snapshot.AssignedTo;
            return true;
        }
        return snapshot.Fields.TryGetValue(fieldRef, out value);
    }

    private static void ValidateDraft(RecoverableWorkItemDraft draft, EditCapability capability)
    {
        var changes = draft.Changes;
        var state = changes.FirstOrDefault(change =>
            string.Equals(change.FieldRef, "System.State", StringComparison.OrdinalIgnoreCase));
        if (state is not null)
        {
            var accompanying = changes
                .Where(change => !ReferenceEquals(change, state))
                .Select(change => new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue))
                .ToArray();
            if (capability.Validate(new StateMove(
                    state.BaselineValue ?? string.Empty,
                    state.ProposedValue ?? string.Empty,
                    accompanying)) is Rejected rejected)
                throw new DraftCommandException(1, rejected.Reason);
            return;
        }

        foreach (var change in changes)
        {
            if (capability.Validate(new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue))
                is Rejected rejected)
                throw new DraftCommandException(1, rejected.Reason);
        }
    }

    private void RenderDraft(RecoverableWorkItemDraft draft, int currentRevision, string kind, string format,
        bool hasLocalChanges = false, BrowserEditingSession? session = null, bool document = false)
    {
        var state = draft.HasConflicts
            ? "conflicted"
            : hasLocalChanges ? "blocked-local-changes"
            : draft.Changes.Count == 0 ? "empty" : "staged";
        if (format == "minimal")
        {
            (stdout ?? Console.Out).WriteLine(string.Join('\t',
                draft.ItemKey.WorkItemId.ToString(CultureInfo.InvariantCulture),
                draft.Generation.ToString(CultureInfo.InvariantCulture),
                draft.BaselineRevision.ToString(CultureInfo.InvariantCulture),
                draft.ObservedRevision.ToString(CultureInfo.InvariantCulture),
                currentRevision.ToString(CultureInfo.InvariantCulture),
                state,
                draft.Changes.Count.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        var changes = draft.Changes.Select(change => new RenderCell(
            $"{change.FieldRef}: {Display(change.BaselineValue)} -> {Display(change.ProposedValue)}",
            new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["fieldRef"] = RenderCell.String(change.FieldRef),
                ["baselineValue"] = NullableCell(change.BaselineValue),
                ["proposedValue"] = NullableCell(change.ProposedValue),
            }))).ToArray();
        var conflicts = draft.Conflicts.Select(conflict => new RenderCell(
            $"{conflict.FieldRef}: observed {Display(conflict.ObservedValue)}; draft {Display(conflict.ProposedValue)}",
            new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["fieldRef"] = RenderCell.String(conflict.FieldRef),
                ["baselineValue"] = NullableCell(conflict.BaselineValue),
                ["observedValue"] = NullableCell(conflict.ObservedValue),
                ["proposedValue"] = NullableCell(conflict.ProposedValue),
            }))).ToArray();

        var key = new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["checkoutRoot"] = RenderCell.String(draft.ItemKey.CheckoutRoot),
            ["connectionId"] = RenderCell.String(draft.ItemKey.ConnectionId),
            ["bindingId"] = RenderCell.String(draft.ItemKey.BindingId),
            ["workItemId"] = RenderCell.Integer(draft.ItemKey.WorkItemId),
        });
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["itemKey"] = new($"#{draft.ItemKey.WorkItemId}", key),
            ["baselineRevision"] = RenderCell.Integer(draft.BaselineRevision),
            ["observedRevision"] = RenderCell.Integer(draft.ObservedRevision),
            ["currentRevision"] = RenderCell.Integer(currentRevision),
            ["generation"] = RenderCell.Integer(draft.Generation),
            ["status"] = RenderCell.String(state),
            ["hasConflicts"] = RenderCell.Boolean(draft.HasConflicts),
            ["changes"] = new(string.Join("; ", draft.Changes.Select(change =>
                $"{change.FieldRef}: {Display(change.BaselineValue)} -> {Display(change.ProposedValue)}")),
                new RenderValue.Array(changes)),
            ["conflicts"] = new(string.Join("; ", draft.Conflicts.Select(conflict =>
                $"{conflict.FieldRef}: observed {Display(conflict.ObservedValue)}; draft {Display(conflict.ProposedValue)}")),
                new RenderValue.Array(conflicts)),
        };

        if (document)
        {
            if (session is null)
                throw new InvalidOperationException("A draft document requires the native editing session.");
            AddDocumentFields(fields, draft, session);
        }

        var nodes = format is "json" or "json-full" or "json-compact"
            ? new RenderNode[] { new RenderNode.Record(document ? "draftDocument" : kind, fields) }
            : [
                new RenderNode.Text($"Draft for work item #{draft.ItemKey.WorkItemId}: {state}"),
                new RenderNode.KeyValue("Generation", RenderCell.Integer(draft.Generation)),
                new RenderNode.KeyValue("Baseline revision", RenderCell.Integer(draft.BaselineRevision)),
                new RenderNode.KeyValue("Observed revision", RenderCell.Integer(draft.ObservedRevision)),
                new RenderNode.KeyValue("Current revision", RenderCell.Integer(currentRevision)),
                new RenderNode.Section("Changes", draft.Changes.Select(change =>
                    (RenderNode)new RenderNode.KeyValue(change.FieldRef,
                        RenderCell.String($"{Display(change.BaselineValue)} -> {Display(change.ProposedValue)}"))).ToArray()),
                new RenderNode.Section("Conflicts", draft.Conflicts.Select(conflict =>
                    (RenderNode)new RenderNode.KeyValue(conflict.FieldRef,
                        RenderCell.String($"observed {Display(conflict.ObservedValue)}; draft {Display(conflict.ProposedValue)}"))).ToArray()),
                new RenderNode.Text("This draft is local and does not authorize or perform an apply."),
            ];
        renderers.GetRenderer(format, stdout ?? Console.Out).Render(new RenderTree.RenderTree(nodes));
    }

    private static void AddDocumentFields(
        IDictionary<string, RenderCell> fields,
        RecoverableWorkItemDraft draft,
        BrowserEditingSession session)
    {
        var observedValues = draft.Changes.Select(change =>
        {
            string? observedValue = null;
            var available = !session.HasLocalChanges
                && session.CurrentValues.TryGetValue(change.FieldRef, out observedValue);
            var persistable = session.Capability.CanEdit(change.FieldRef);
            var eligibilityReason = !available && session.HasLocalChanges
                ? "Current value is obscured by unrelated local pending work."
                : !available
                    ? "Field is absent from the native form or its value is not carried by Twig."
                    : !persistable
                        ? "Field is not persistable by the native edit capability."
                        : null;
            return new
            {
                Change = change,
                ObservedValue = observedValue,
                ObservedValueAvailable = available,
                Persistable = persistable,
                EligibilityReason = eligibilityReason,
            };
        }).ToArray();

        var fieldsPersistable = observedValues.All(value => value.Persistable);
        var rules = EvaluateRules(draft, session);
        var reconciliationState = draft.HasConflicts
            ? "conflicted"
            : session.HasLocalChanges
                ? "blocked-local-work"
                : observedValues.Any(value => !value.ObservedValueAvailable)
                    ? "field-unavailable"
                    : !fieldsPersistable
                        ? "capability-refused"
                        : rules.Status switch
                        {
                            "rejected" => "rules-refused",
                            "unknown" => "rules-unknown",
                            _ => "current",
                        };
        var observationCompleteness = session.HasLocalChanges
            ? "blocked-by-local-work"
            : observedValues.Any(value => !value.ObservedValueAvailable)
                ? "incomplete-for-draft-effects"
                : "complete-for-draft-effects";

        var exportedFields = observedValues.Select(value => new RenderCell(
            value.Change.FieldRef,
            new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["fieldRef"] = RenderCell.String(value.Change.FieldRef),
                ["baselineValue"] = NullableCell(value.Change.BaselineValue),
                ["observedValue"] = NullableCell(value.ObservedValue),
                ["observedValueAvailable"] = RenderCell.Boolean(value.ObservedValueAvailable),
                ["proposedValue"] = NullableCell(value.Change.ProposedValue),
                ["nativePersistable"] = RenderCell.Boolean(value.Persistable),
                ["nativeCapabilitySource"] = RenderCell.String("native-edit-sink"),
                ["eligibilityReason"] = value.EligibilityReason is null
                    ? new RenderCell(string.Empty, new RenderValue.Null())
                    : RenderCell.String(value.EligibilityReason),
                ["matchesBaseline"] = RenderCell.Boolean(
                    value.ObservedValueAvailable
                    && string.Equals(value.Change.BaselineValue, value.ObservedValue, StringComparison.Ordinal)),
            }))).ToArray();

        fields["draftDocumentVersion"] = RenderCell.Integer(1);
        fields["draftStatus"] = fields["status"];
        fields["status"] = RenderCell.String(reconciliationState);
        fields["reconciliationState"] = RenderCell.String(reconciliationState);
        fields["freshness"] = RenderCell.String("cached");
        fields["observationCompleteness"] = RenderCell.String(observationCompleteness);
        fields["localPendingWork"] = RenderCell.Boolean(session.HasLocalChanges);
        fields["fields"] = new(string.Join("; ", observedValues.Select(value => value.Change.FieldRef)),
            new RenderValue.Array(exportedFields));
        fields["eligibility"] = new("Native draft eligibility; it does not authorize an apply.",
            new RenderValue.Object(new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["fieldsPersistable"] = RenderCell.Boolean(fieldsPersistable),
                ["rules"] = RenderCell.String(rules.Status),
                ["rulesReason"] = rules.Reason is null ? new RenderCell(string.Empty, new RenderValue.Null())
                    : RenderCell.String(rules.Reason),
                ["serverIsFinalAuthority"] = RenderCell.Boolean(true),
                ["authorized"] = RenderCell.Boolean(false),
                ["inspectionOnly"] = RenderCell.Boolean(true),
            }));
    }

    private static (string Status, string? Reason) EvaluateRules(
        RecoverableWorkItemDraft draft,
        BrowserEditingSession session)
    {
        var state = draft.Changes.FirstOrDefault(change =>
            string.Equals(change.FieldRef, "System.State", StringComparison.OrdinalIgnoreCase));
        if (state is null)
            return ("not-applicable", null);
        if (!session.ProcessRulesAvailable)
            return ("unknown", "Native process rules are unavailable for this editing session.");

        try
        {
            ValidateDraft(draft, session.Capability);
            return ("accepted", null);
        }
        catch (DraftCommandException ex)
        {
            return ("rejected", ex.Message);
        }
    }

    private void WriteError(string outputFormat, string message)
    {
        var format = OutputFormats.Normalize(outputFormat);
        CommandError.Write(renderers, ctx.StderrWriter, format ?? "human", message);
    }

    private static string ValidateFormat(string outputFormat)
    {
        var format = OutputFormats.Normalize(outputFormat);
        if (format is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
            throw new ArgumentException("Draft commands support human, json, and minimal output, not ids.");
        return format;
    }

    private static string TelemetryFormat(string outputFormat)
        => OutputFormats.Normalize(outputFormat) ?? OutputFormatterFactory.DefaultFormat;

    private static RenderCell NullableCell(string? value)
        => value is null ? new RenderCell(string.Empty, new RenderValue.Null()) : RenderCell.String(value);

    private static string Display(string? value) => value is null ? "(empty)" : value;

    private sealed record OpenedDraftItem(BrowserEditingSession Session, CanvasDetailExport Document);

    private sealed class DraftCommandException(int exitCode, string message) : Exception(message)
    {
        internal int ExitCode { get; } = exitCode;
    }
}
