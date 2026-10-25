using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Twig.Browser;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;
using Twig.Infrastructure.Persistence;

namespace Twig.Commands;

/// <summary>Inspects existing recoverable drafts; only observation metadata may be written.</summary>
internal sealed class DraftCommand(CommandContext ctx, BrowserReadModule browser,
    BrowserEditingModule editing, BrowserOperationModule operations,
    IRecoverableWorkItemDraftStore drafts, RendererFactory renderers, TextWriter? stdout = null,
    SqliteCacheStore? cache = null, IPlanOriginProvider? origins = null, IProcessRuleProvider? rules = null)
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

            QualifiedWorkItemKey? evidenceKey = null;
            string? processFingerprint = null;
            IReadOnlyList<ProcessRule>? retainedRules = null;
            string? inspectedType = null;
            BrowserCapture? inspectionCapture = null;
            if (document && cache is not null)
            {
                var admitted = await operations.ExecuteAsync<BrowserCapture>(null, (capture, _) =>
                    Task.FromResult(capture), ct);
                if (admitted.Failure is { } failure) throw FailureException(failure, ct);
                inspectionCapture = admitted.Value ?? throw new InvalidOperationException("Native inspection did not admit a qualified key.");
                evidenceKey = new(inspectionCapture.Origin.WorktreeRoot, inspectionCapture.Origin.Binding.ConnectionRef,
                    inspectionCapture.BindingId, id);
                var evidence = new SqliteDraftCaptureEvidenceStore(cache);
                // Revoke only this admitted key before network acquisition. A failed or
                // canceled inspection can never leave an older positive receipt usable.
                evidence.Invalidate(evidenceKey);
                processFingerprint = evidence.ProcessFingerprint();
                (inspectedType, retainedRules) = await editing.InspectAsync(id, rules, ct);
            }
            var read = await browser.ReadDetailAsync(new(id, Ansi: false, Document: true, Captured: inspectionCapture), ct);
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
            if (evidenceKey is not null && cache is not null && inspection.Session.ServedLayout is { } served)
            {
                if (evidenceKey != inspection.Draft.ItemKey)
                    throw new InvalidOperationException("Native inspection changed qualified identity; no evidence was recorded.");
                if (inspectedType != inspection.Session.Snapshot.TypeName)
                    throw new InvalidOperationException("metadata-stale: native item type changed during explicit inspection.");
                var origin = await (origins ?? throw new InvalidOperationException("Native capture origin provider is unavailable."))
                    .GetOriginAsync(ct);
                await operations.EnsureCurrentAsync(inspection.Session.Capture, ct);
                if (!await editing.MatchesBaselineAsync(inspection.Session, ct))
                    throw new InvalidOperationException("Native inspection baseline changed before evidence was recorded.");
                var bench = (await new SqliteBenchRepository(cache).GetAllAsync(ct))
                    .SingleOrDefault(candidate => candidate.Id.ToString(CultureInfo.InvariantCulture) == inspection.Session.Capture.BenchId);
                if (bench is null || !inspection.Session.Capture.Matches(bench))
                    throw new InvalidOperationException("bench-stale: inspected native Bench changed before evidence save.");
                await new SqliteDraftCaptureEvidenceStore(cache).RecordAsync(new(1, evidenceKey, origin,
                    inspection.Session.Capture.StorageGeneration, inspection.Session.Snapshot.TypeName,
                    inspection.Session.Revision, processFingerprint!,
                    SqliteDraftCaptureEvidenceStore.SnapshotFingerprint(inspection.Session.Snapshot),
                    served, retainedRules, bench, SqliteDraftCaptureEvidenceStore.RequiresNormalization(bench)
                        ? inspection.Session.Capture.Origin.Identity.AccountName : null), inspection.Session.Snapshot, ct);
            }
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

    internal async Task<int> CaptureAsync(int id, string bench, string checkoutRoot, string connectionId,
        string bindingId, string identityId, long generation, int revision,
        string outputFormat = OutputFormatterFactory.DefaultFormat, string? recover = null, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(outputFormat);
        var exit = 1;
        PlanJournal? committed = null;
        try
        {
            if (format is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
                throw new ArgumentException("Draft capture supports human, json, and minimal output.");
            if (id <= 0 || generation < 0 || revision <= 0 || string.IsNullOrWhiteSpace(bench)
                || string.IsNullOrWhiteSpace(checkoutRoot) || string.IsNullOrWhiteSpace(connectionId)
                || string.IsNullOrWhiteSpace(bindingId) || string.IsNullOrWhiteSpace(identityId))
                throw new ArgumentException("Capture requires a published ID, explicit Bench, qualified native identity, generation and revision.");
            var local = cache ?? throw new InvalidOperationException("Native draft capture storage is unavailable.");
            var journals = new SqlitePlanJournalRepository(local);
            var key = new QualifiedWorkItemKey(checkoutRoot, connectionId, bindingId, id);
            var result = await operations.ExecuteExplicitAsync(bench, async (capture, token) =>
            {
                if (capture.Origin.WorktreeRoot != checkoutRoot || capture.Origin.Binding.ConnectionRef != connectionId)
                    throw new InvalidOperationException("Draft qualified origin changed; intent was retained.");
                var origin = await (origins ?? throw new InvalidOperationException("Native origin is unavailable.")).GetOriginAsync(token);
                var draft = await drafts.GetAsync(key, token)
                    ?? throw new InvalidOperationException("No recoverable draft exists for this exact native identity.");
                if (draft.Generation != generation || draft.BaselineRevision != revision || draft.ObservedRevision != revision)
                    throw new InvalidOperationException("draft-stale: exact generation or revision changed; no retry was attempted.");
                var evidence = new SqliteDraftCaptureEvidenceStore(local).Read(key);
                if (!capture.Matches(evidence.Bench))
                    throw new InvalidOperationException("bench-stale: explicit capture Bench differs from the inspected native Bench.");
                if (recover is not null)
                {
                    var retained = await journals.GetAsync(recover, token)
                        ?? throw new InvalidOperationException("No retained capture has that digest.");
                    if (retained.DraftCapture is not { Version: 2 } bound || bound.Origin != origin
                        || bound.Draft.ItemKey != key || bound.Draft.Generation != generation
                        || bound.CurrentRevision != revision
                        || JsonSerializer.Serialize(bound.Draft, Infrastructure.Serialization.TwigJsonContext.Default.RecoverableWorkItemDraft)
                            != JsonSerializer.Serialize(draft, Infrastructure.Serialization.TwigJsonContext.Default.RecoverableWorkItemDraft))
                        throw new InvalidOperationException("Recovery does not match the exact retained capture; no import was attempted.");
                    if ((await journals.CaptureIssueAsync(retained, origin, token)).Code != "draft-capture-inspection-only")
                        throw new InvalidOperationException("Retained capture is stale; export recovery refused without changing intent.");
                    journals.SetCaptureExportState(retained.Digest, complete: false);
                    committed = retained;
                    return retained;
                }
                // Read cached evidence before anything that could request metadata. This
                // command never calls editor Open, a work-metadata provider, or generic
                // proposal Preview. Native identity admission above may attest a cold provider.
                var snapshot = await new SqliteWorkItemRepository(local, new Domain.Services.WorkItemMapper())
                    .GetSnapshotByIdAsync(id, token)
                    ?? throw new InvalidOperationException("The native work item is not cached.");
                await operations.EnsureCurrentAsync(capture, token);
                var workspace = new PlanWorkspace { Organization = ctx.Config.Organization, Project = ctx.Config.Project };
                var directory = Path.Combine(checkoutRoot, ".twig", "proposals");
                var journal = await new SqliteDraftProposalCaptureStore(local).CaptureObservedAsync(
                    draft, snapshot, origin, workspace, directory, SqliteDraftCaptureEvidenceStore.Fingerprint(evidence), token);
                committed = journal;
                journals.SetCaptureExportState(journal.Digest, complete: false);
                return journal;
            }, ct, expectedBinding: bindingId, expectedIdentity: identityId);
            if (result.Failure is { } refused) throw FailureException(refused, ct);
            var retained = result.Value ?? throw new InvalidOperationException("No native capture was retained.");
            await ExportAsync(retained, ct);
            ct.ThrowIfCancellationRequested();
            journals.SetCaptureExportState(retained.Digest, complete: true);
            var fields = new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(retained.Digest), ["file"] = RenderCell.String(retained.SourcePath),
                ["status"] = RenderCell.String("exported-local-intent"), ["generation"] = RenderCell.Integer(generation),
                ["authorized"] = RenderCell.Boolean(false), ["canApply"] = RenderCell.Boolean(false),
            };
            if (format == "minimal") (stdout ?? Console.Out).WriteLine($"{retained.Digest}\t{retained.SourcePath}");
            else renderers.GetRenderer(format, stdout ?? Console.Out).Render(new RenderTree.RenderTree(
                [new RenderNode.Record("draft capture", fields)]));
            return exit = 0;
        }
        catch (ArgumentException ex) when (committed is null)
        {
            CommandError.Write(renderers, ctx.StderrWriter, format ?? "human", ex.Message);
            return exit = 2;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || committed is not null)
        {
            var message = committed is null ? ex.Message
                : $"export-incomplete: capture {committed.Digest} is retained at {committed.SourcePath}; recover using --recover {committed.Digest}. {ex.Message}";
            CommandError.Write(renderers, ctx.StderrWriter, format ?? "human", message);
            return exit;
        }
        finally { TelemetryHelper.TrackCommand(ctx.TelemetryClient, "draft capture", format ?? "human", exit, started); }
    }

    internal static async Task ExportAsync(PlanJournal journal, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(journal.CanonicalJson);
        var path = journal.SourcePath;
        if (File.Exists(path))
        {
            if (!(await File.ReadAllBytesAsync(path, ct)).SequenceEqual(bytes))
                throw new IOException("The export path contains different bytes; it was not overwritten.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if (!(await File.ReadAllBytesAsync(path, ct)).SequenceEqual(bytes))
                    throw new IOException("Another writer created different export bytes; nothing was overwritten.");
            }
            if (!(await File.ReadAllBytesAsync(path, ct)).SequenceEqual(bytes))
                throw new IOException("Native capture export readback did not match canonical bytes.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        fields["nativeCaptureContext"] = ObjectCell(
            ("benchId", RenderCell.String(session.Capture.BenchId)),
            ("identityId", RenderCell.String(session.Capture.IdentityId)),
            ("servedLayoutObserved", RenderCell.Boolean(session.ServedLayout is not null)),
            ("serverMetadataRevisionKnown", RenderCell.Boolean(false)));
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
