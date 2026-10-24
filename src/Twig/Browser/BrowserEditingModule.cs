using System.Collections.ObjectModel;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Persistence;

namespace Twig.Browser;

/// <summary>Document-led, locally staged editing. This module has no publication dependency.</summary>
internal sealed class BrowserEditingModule(BrowserOperationModule operations, IWorkItemRepository repository,
    IPendingChangeStore pendingChanges, DetailDocumentSource documents, IProcessConfigurationProvider process)
{
    private readonly WorkItemMapper _mapper = new();

    internal Task<BrowserOperationResult<BrowserEditingSession>> OpenAsync(int id, BrowserCapture captured,
        CancellationToken ct = default) => operations.ExecuteAsync(captured, async (capture, token) =>
    {
        if (id == 0)
            throw new BrowserReadException(BrowserReadFailureKind.Usage, "A nonzero work-item ID or negative seed alias is required.");
        var item = await repository.GetByIdAsync(id, token)
            ?? throw new BrowserReadException(BrowserReadFailureKind.Unavailable, $"Work item #{id} is not cached. Opening an editor does not refresh it.");
        var snapshot = Snapshot(item);
        var notes = item.PendingNotes.ToArray();
        // Project a copy: a repository may hand out a mutable aggregate while layout awaits I/O.
        var document = await documents.GetAsync(_mapper.Map(snapshot), token);
        var pending = (await pendingChanges.GetChangesAsync(id, token)).ToArray();
        ProcessConfiguration? configuration;
        try { configuration = process.GetConfiguration(); }
        catch (InvalidOperationException) { configuration = null; }
        var sink = new PendingChangeStoreSink(pendingChanges).BoundTo(id, snapshot.Revision);
        var session = new BrowserEditingSession(capture, snapshot, notes, pending, document,
            new EditCapability(sink, item.Type, item.IsSeed, configuration),
            processRulesAvailable: configuration?.TypeConfigs.ContainsKey(item.Type) == true);
        if (!await MatchesBaselineAsync(session, token))
            throw new BrowserReadException(BrowserReadFailureKind.Unavailable, "Local work changed while the editor opened. Reopen it; nothing was staged.");
        return session;
    }, ct);

    internal Task<BrowserOperationResult<SubmitOutcome>> StageAsync(BrowserEditingSession session,
        IReadOnlyDictionary<string, string> changes, CancellationToken ct = default)
    {
        // Own the input across awaits; a failed/canceled save never edits the host's buffer.
        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (!requested.TryAdd(change.Key, change.Value))
                return Task.FromResult(new BrowserOperationResult<SubmitOutcome>(
                    new Refused($"Field '{change.Key}' was specified more than once."), null, session.Capture));
        }
        return operations.ExecuteAsync<SubmitOutcome>(session.Capture, async (capture, token) =>
        {
            var edits = new List<FieldEdit>(requested.Count);
            foreach (var change in requested)
            {
                if (!session.Capability.CanEdit(change.Key))
                    return new Refused($"Field '{change.Key}' is not persistable by this sink.");
                if (!session.CurrentValues.TryGetValue(change.Key, out var prior))
                    return new Refused($"Field '{change.Key}' is absent from the served form or its value is not carried by Twig. Reopen a supported form before editing it.");
                if (!string.Equals(prior ?? string.Empty, change.Value, StringComparison.Ordinal))
                    edits.Add(new FieldEdit(session.CanonicalField(change.Key), prior, change.Value));
            }
            if (!await MatchesBaselineAsync(session, token))
                return new Refused("The cached item revision, fields, notes or pending changes changed since this editor opened. Your buffer is retained; reopen and reconcile before staging.");
            if (edits.Count == 0) return new Saved(session.Revision);

            var state = edits.Find(edit => string.Equals(edit.FieldRef, "System.State", StringComparison.OrdinalIgnoreCase));
            StateMove? move = state is null ? null : new StateMove(state.PriorValue ?? string.Empty,
                state.ProposedValue ?? string.Empty, edits.Where(edit => !ReferenceEquals(edit, state)).ToArray());
            if (move is not null)
            {
                if (session.Capability.Validate(move) is Rejected rejected) return new Refused(rejected.Reason);
            }
            else
            {
                foreach (var edit in edits)
                    if (session.Capability.Validate(edit) is Rejected rejected) return new Refused(rejected.Reason);
            }

            // This fence immediately precedes the single local SQLite batch. It is not an
            // atomic transaction spanning configuration, registry, cache and durable stores.
            await operations.EnsureCurrentAsync(capture, token);
            if (!await MatchesBaselineAsync(session, token))
                return new Refused("Local pending work changed before staging. Your buffer is retained; no changes were written.");
            var sink = new PendingChangeStoreSink(pendingChanges).BoundTo(session.WorkItemId, session.Revision);
            var outcome = move is null
                ? await sink.SubmitBatchAsync(edits, token)
                : await sink.SubmitAsync(move, token);
            if (outcome is Saved)
            {
                // Record precisely our appended batch, never adopt a concurrent writer's rows.
                // A failed final origin fence cannot erase a successfully committed local batch.
                session.Accept(edits, move);
            }
            return outcome;
        }, ct);
    }

    private WorkItemSnapshot Snapshot(WorkItem item)
    {
        var snapshot = _mapper.ToSnapshot(item);
        return snapshot with
        {
            Fields = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(snapshot.Fields, StringComparer.OrdinalIgnoreCase))
        };
    }

    private async Task<bool> MatchesBaselineAsync(BrowserEditingSession session, CancellationToken ct)
    {
        var current = await repository.GetByIdAsync(session.WorkItemId, ct);
        if (current is null || !session.Matches(Snapshot(current), current.PendingNotes)) return false;
        return session.MatchesPending(await pendingChanges.GetChangesAsync(session.WorkItemId, ct));
    }
}

/// <summary>One exact local baseline plus saved overlays, retained independently of a live repository.</summary>
internal sealed class BrowserEditingSession
{
    private readonly WorkItemSnapshot _baseline;
    private readonly PendingNote[] _notes;
    private PendingChangeRecord[] _pending;
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);

    internal BrowserEditingSession(BrowserCapture capture, WorkItemSnapshot baseline, PendingNote[] notes,
        PendingChangeRecord[] pending, WorkItemDetailDocument document, EditCapability capability,
        bool processRulesAvailable)
    {
        Capture = capture;
        _baseline = baseline;
        _notes = notes;
        _pending = pending;
        Snapshot = baseline;
        Document = document;
        Capability = capability;
        ProcessRulesAvailable = processRulesAvailable;
        foreach (var control in document.SystemControls.Concat(document.Pages.Where(page => page.CarriesFieldControls)
                     .SelectMany(page => page.AllGroups).SelectMany(group => group.Controls)))
        {
            if (control.IsContribution || control.Value is null || control.Value.State == DetailFieldState.NotCarriedByTwig) continue;
            _values.TryAdd(control.Id, control.Value.Full);
            _canonical.TryAdd(control.Id, control.Id);
        }
        foreach (var change in pending)
        {
            if (change.FieldName is not { } field || !_values.ContainsKey(field) || !IsFieldChange(change.ChangeType)) continue;
            _values[field] = change.NewValue;
        }
        CurrentValues = new ReadOnlyDictionary<string, string?>(_values);
    }

    internal BrowserCapture Capture { get; }
    internal int WorkItemId => _baseline.Id;
    internal int Revision => _baseline.Revision;
    internal WorkItemDetailDocument Document { get; }
    internal EditCapability Capability { get; }
    internal bool ProcessRulesAvailable { get; }
    internal WorkItemSnapshot Snapshot { get; }
    internal IReadOnlyDictionary<string, string?> CurrentValues { get; }
    internal bool HasLocalChanges => _baseline.IsDirty || _notes.Length > 0 || _pending.Length > 0;
    internal string CanonicalField(string field) => _canonical[field];

    internal bool Matches(WorkItemSnapshot current, IReadOnlyList<PendingNote> notes)
        => (_baseline with { Fields = current.Fields }) == current
            && _baseline.Fields.Count == current.Fields.Count
            && _baseline.Fields.All(pair => current.Fields.TryGetValue(pair.Key, out var value) && pair.Value == value)
            && _notes.SequenceEqual(notes);
    internal bool MatchesPending(IReadOnlyList<PendingChangeRecord> pending) => _pending.SequenceEqual(pending);

    internal void Accept(IReadOnlyList<FieldEdit> edits, StateMove? move)
    {
        var appended = new List<PendingChangeRecord>(edits.Count);
        if (move is not null)
            appended.Add(new(WorkItemId, "state", "System.State", move.FromState, move.ToState));
        foreach (var edit in edits)
        {
            _values[edit.FieldRef] = edit.ProposedValue;
            if (move is not null && string.Equals(edit.FieldRef, "System.State", StringComparison.OrdinalIgnoreCase)) continue;
            appended.Add(new(WorkItemId, "field", edit.FieldRef, edit.PriorValue, edit.ProposedValue));
        }
        _pending = [.. _pending, .. appended];
    }

    private static bool IsFieldChange(string kind) => kind is "field" or "state" or "set_field";
}
