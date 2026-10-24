using System.Diagnostics;
using System.Globalization;
using Twig.Browser;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Inspects existing recoverable drafts; only observation metadata may be written.</summary>
internal sealed class DraftCommand(CommandContext ctx, BrowserReadModule browser,
    BrowserEditingModule editing, BrowserOperationModule operations,
    IRecoverableWorkItemDraftStore drafts, RendererFactory renderers, TextWriter? stdout = null)
{
    internal async Task<int> ShowAsync(int id,
        string outputFormat = OutputFormatterFactory.DefaultFormat, bool document = false,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(outputFormat);
        try
        {
            if (format is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
                throw new ArgumentException("Draft inspection supports human, json, and minimal output, not ids.");
            if (document && format is not ("json" or "json-full" or "json-compact"))
                throw new ArgumentException("--document requires JSON output (-o json).");

            var read = await browser.ReadDetailAsync(new(id, Ansi: false, Document: true), ct);
            if (read.Failure is { } readFailure)
                throw FailureException(readFailure, ct);
            var observation = read.Observation
                ?? throw new InvalidOperationException("The native detail read returned no observation.");
            var semantic = observation.Document
                ?? throw new InvalidOperationException("The native detail read returned no semantic document.");
            var itemKey = new QualifiedWorkItemKey(semantic.ItemKey.CheckoutRoot,
                semantic.ItemKey.ConnectionId, semantic.ItemKey.BindingId, semantic.ItemKey.WorkItemId);
            if (itemKey.WorkItemId != id)
                throw new InvalidOperationException("The native detail identity does not match the requested work item.");
            var opened = await editing.OpenAsync(id, observation.Capture, ct);
            if (opened.Failure is { } editFailure)
                throw FailureException(editFailure, ct);
            var session = opened.Value
                ?? throw new InvalidOperationException("The native editor returned no observation session.");
            if (session.WorkItemId != id || session.Revision != observation.Revision)
                throw new InvalidOperationException(
                    "The item changed between native read and editor open. No draft was changed; inspect again.");

            var result = await operations.ExecuteAsync(observation.Capture, async (_, token) =>
            {
                if (!await editing.MatchesBaselineAsync(session, token))
                    throw new InvalidOperationException(
                        "The cached item or pending work changed before draft inspection. Intent was retained; inspect again.");
                var draft = await drafts.GetAsync(itemKey, token)
                    ?? throw new InvalidOperationException($"No recoverable draft exists for work item #{id}.");
                var observed = Observe(draft, session);
                if (observed.Generation != draft.Generation)
                {
                    await operations.EnsureCurrentAsync(session.Capture, token);
                    if (!await editing.MatchesBaselineAsync(session, token))
                        throw new InvalidOperationException(
                            "The cached item or pending work changed before observation metadata was saved. Intent was retained; inspect again.");
                    if (!await drafts.TrySaveAsync(observed, draft.Generation, token))
                        throw new InvalidOperationException(
                            "The recoverable draft changed in another surface. No retry was attempted; inspect again.");
                }
                return new Inspection(observed, session);
            }, ct);
            if (result.Failure is { } refused)
                throw FailureException(refused, ct);
            var inspection = result.Value
                ?? throw new InvalidOperationException("The native draft inspection returned no document.");
            Render(inspection.Draft, inspection.Session, format, document);
            return exit = 0;
        }
        catch (ArgumentException ex)
        {
            exit = 2;
            CommandError.Write(renderers, ctx.StderrWriter, format ?? "human", ex.Message);
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exit = ex is BrowserReadException { Kind: BrowserReadFailureKind.Usage } ? 2 : 1;
            CommandError.Write(renderers, ctx.StderrWriter, format ?? "human", ex.Message);
            return exit;
        }
        finally
        {
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "draft show", format ?? "human", exit, started);
        }
    }

    private static RecoverableWorkItemDraft Observe(RecoverableWorkItemDraft draft, BrowserEditingSession session)
    {
        if (session.HasLocalChanges)
            return draft.Observe(session.Revision, draft.Conflicts);
        var conflicts = new List<DraftFieldConflict>();
        foreach (var change in draft.Changes)
        {
            if (!TryGetObservedValue(session.Snapshot, change.FieldRef, out var value))
            {
                conflicts.AddRange(draft.Conflicts.Where(conflict =>
                    string.Equals(conflict.FieldRef, change.FieldRef, StringComparison.OrdinalIgnoreCase)));
            }
            else if (!string.Equals(change.BaselineValue, value, StringComparison.Ordinal))
                conflicts.Add(new DraftFieldConflict(change.FieldRef, change.BaselineValue, value, change.ProposedValue));
        }
        var ordered = conflicts.OrderBy(conflict => conflict.FieldRef, StringComparer.Ordinal).ToArray();
        return draft.Observe(session.Revision, ordered);
    }

    private static bool TryGetObservedValue(WorkItemSnapshot snapshot, string fieldRef, out string? value)
    {
        if (fieldRef.Equals("System.Title", StringComparison.OrdinalIgnoreCase)) { value = snapshot.Title; return true; }
        if (fieldRef.Equals("System.State", StringComparison.OrdinalIgnoreCase)) { value = snapshot.State; return true; }
        if (fieldRef.Equals("System.AssignedTo", StringComparison.OrdinalIgnoreCase)) { value = snapshot.AssignedTo; return true; }
        return snapshot.Fields.TryGetValue(fieldRef, out value);
    }

    private void Render(RecoverableWorkItemDraft draft, BrowserEditingSession session, string format, bool document)
    {
        var localStatus = draft.Changes.Count == 0 ? "empty" : "staged";
        var state = draft.HasConflicts ? "conflicted" : session.HasLocalChanges ? "blocked-local-work" : localStatus;
        if (format == "minimal")
        {
            (stdout ?? Console.Out).WriteLine(string.Join('\t',
                draft.ItemKey.WorkItemId.ToString(CultureInfo.InvariantCulture),
                draft.Generation.ToString(CultureInfo.InvariantCulture),
                draft.BaselineRevision.ToString(CultureInfo.InvariantCulture),
                draft.ObservedRevision.ToString(CultureInfo.InvariantCulture),
                session.Revision.ToString(CultureInfo.InvariantCulture), state,
                draft.Changes.Count.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["itemKey"] = ObjectCell(
                ("checkoutRoot", RenderCell.String(draft.ItemKey.CheckoutRoot)),
                ("connectionId", RenderCell.String(draft.ItemKey.ConnectionId)),
                ("bindingId", RenderCell.String(draft.ItemKey.BindingId)),
                ("workItemId", RenderCell.Integer(draft.ItemKey.WorkItemId))),
            ["baselineRevision"] = RenderCell.Integer(draft.BaselineRevision),
            ["observedRevision"] = RenderCell.Integer(draft.ObservedRevision),
            ["currentRevision"] = RenderCell.Integer(session.Revision),
            ["generation"] = RenderCell.Integer(draft.Generation),
            ["status"] = RenderCell.String(state),
            ["hasConflicts"] = RenderCell.Boolean(draft.HasConflicts),
            ["changes"] = ArrayCell(draft.Changes.Select(change => ObjectCell(
                ("fieldRef", RenderCell.String(change.FieldRef)),
                ("baselineValue", NullableCell(change.BaselineValue)),
                ("proposedValue", NullableCell(change.ProposedValue))))),
            ["conflicts"] = ArrayCell(draft.Conflicts.Select(conflict => ObjectCell(
                ("fieldRef", RenderCell.String(conflict.FieldRef)),
                ("baselineValue", NullableCell(conflict.BaselineValue)),
                ("observedValue", NullableCell(conflict.ObservedValue)),
                ("proposedValue", NullableCell(conflict.ProposedValue))))),
        };
        if (document) AddDocumentFields(fields, draft, session, localStatus);
        var nodes = format is "json" or "json-full" or "json-compact"
            ? new RenderNode[] { new RenderNode.Record(document ? "draftDocument" : "draft show", fields) }
            : [
                new RenderNode.Text($"Draft for work item #{draft.ItemKey.WorkItemId}: {state}"),
                new RenderNode.KeyValue("Generation", fields["generation"]),
                new RenderNode.KeyValue("Baseline revision", fields["baselineRevision"]),
                new RenderNode.KeyValue("Observed revision", fields["observedRevision"]),
                new RenderNode.KeyValue("Current revision", fields["currentRevision"]),
                new RenderNode.Section("Changes", draft.Changes.Select(change => (RenderNode)new RenderNode.KeyValue(
                    change.FieldRef, RenderCell.String($"{Display(change.BaselineValue)} -> {Display(change.ProposedValue)}"))).ToArray()),
                new RenderNode.Section("Conflicts", draft.Conflicts.Select(conflict => (RenderNode)new RenderNode.KeyValue(
                    conflict.FieldRef, RenderCell.String($"observed {Display(conflict.ObservedValue)}; draft {Display(conflict.ProposedValue)}"))).ToArray()),
                new RenderNode.Text("Inspection may record local observation metadata; it never authorizes or performs an apply."),
            ];
        renderers.GetRenderer(format, stdout ?? Console.Out).Render(new RenderTree.RenderTree(nodes));
    }

    private static void AddDocumentFields(IDictionary<string, RenderCell> fields,
        RecoverableWorkItemDraft draft, BrowserEditingSession session, string localStatus)
    {
        var observed = draft.Changes.Select(change =>
        {
            string? value = null;
            var available = !session.HasLocalChanges && session.CurrentValues.TryGetValue(change.FieldRef, out value);
            var persistable = session.Capability.CanEdit(change.FieldRef);
            var reason = !available && session.HasLocalChanges ? "Current value is obscured by unrelated local pending work."
                : !available ? "Field is absent from the native form or its value is not carried by Twig."
                : !persistable ? "Field is not persistable by the native edit capability." : null;
            return (Change: change, Value: value, Available: available, Persistable: persistable, Reason: reason);
        }).ToArray();
        var fieldsPersistable = observed.All(field => field.Persistable);
        var rules = EvaluateRules(draft, session);
        var state = draft.HasConflicts ? "conflicted" : session.HasLocalChanges ? "blocked-local-work"
            : observed.Any(field => !field.Available) ? "field-unavailable"
            : !fieldsPersistable ? "capability-refused"
            : rules.Status == "rejected" ? "rules-refused" : rules.Status == "unknown" ? "rules-unknown" : "current";
        fields["draftDocumentVersion"] = RenderCell.Integer(1);
        fields["draftStatus"] = RenderCell.String(localStatus);
        fields["status"] = fields["reconciliationState"] = RenderCell.String(state);
        fields["freshness"] = RenderCell.String("cached");
        fields["observationCompleteness"] = RenderCell.String(session.HasLocalChanges ? "blocked-by-local-work"
            : observed.Any(field => !field.Available) ? "incomplete-for-draft-effects" : "complete-for-draft-effects");
        fields["localPendingWork"] = RenderCell.Boolean(session.HasLocalChanges);
        fields["fields"] = ArrayCell(observed.Select(field => ObjectCell(
            ("fieldRef", RenderCell.String(field.Change.FieldRef)),
            ("baselineValue", NullableCell(field.Change.BaselineValue)),
            ("observedValue", NullableCell(field.Value)),
            ("observedValueAvailable", RenderCell.Boolean(field.Available)),
            ("proposedValue", NullableCell(field.Change.ProposedValue)),
            ("nativePersistable", RenderCell.Boolean(field.Persistable)),
            ("nativeCapabilitySource", RenderCell.String("native-edit-sink")),
            ("eligibilityReason", NullableCell(field.Reason)),
            ("matchesBaseline", RenderCell.Boolean(field.Available
                && string.Equals(field.Change.BaselineValue, field.Value, StringComparison.Ordinal))))));
        fields["eligibility"] = ObjectCell(
            ("fieldsPersistable", RenderCell.Boolean(fieldsPersistable)),
            ("rules", RenderCell.String(rules.Status)),
            ("rulesReason", NullableCell(rules.Reason)),
            ("serverIsFinalAuthority", RenderCell.Boolean(true)),
            ("authorized", RenderCell.Boolean(false)),
            ("inspectionOnly", RenderCell.Boolean(true)));
    }

    private static (string Status, string? Reason) EvaluateRules(RecoverableWorkItemDraft draft, BrowserEditingSession session)
    {
        var state = draft.Changes.FirstOrDefault(change =>
            change.FieldRef.Equals("System.State", StringComparison.OrdinalIgnoreCase));
        if (state is null) return ("not-applicable", null);
        if (!session.ProcessRulesAvailable)
            return ("unknown", "Native process rules are unavailable for this editing session.");
        if (state.ProposedValue is null) return ("rejected", "A state transition cannot clear System.State.");
        var accompanying = draft.Changes.Where(change => !ReferenceEquals(change, state))
            .Select(change => new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue)).ToArray();
        var result = session.Capability.Validate(new StateMove(state.BaselineValue ?? string.Empty, state.ProposedValue, accompanying));
        return result is Rejected rejected ? ("rejected", rejected.Reason) : ("accepted", null);
    }

    private static RenderCell ObjectCell(params (string Key, RenderCell Value)[] fields)
        => new(string.Empty, new RenderValue.Object(fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal)));
    private static RenderCell ArrayCell(IEnumerable<RenderCell> cells) => new(string.Empty, new RenderValue.Array(cells.ToArray()));
    private static RenderCell NullableCell(string? value) => value is null ? new(string.Empty, new RenderValue.Null()) : RenderCell.String(value);
    private static string Display(string? value) => value is null ? "(null)" : value;
    private static Exception FailureException(BrowserReadFailure failure, CancellationToken ct)
        => failure.Kind == BrowserReadFailureKind.Canceled
            ? new OperationCanceledException(failure.Message, ct)
            : new BrowserReadException(failure.Kind, failure.Message);
    private sealed record Inspection(RecoverableWorkItemDraft Draft, BrowserEditingSession Session);
}
