using Twig.Domain.Aggregates;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Projections;

/// <summary>
/// The editing half of the contract: which fields accept input, which states may be moved to,
/// and whether a proposed change is well-formed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is acquired SEPARATELY from <see cref="WorkItemDetailDocument"/>, and correlated to
/// its controls by field reference name.</b> A read-only host never constructs one and never
/// learns the editing vocabulary exists. There is deliberately <b>no null-means-read-only mode
/// switch</b> — a host either has a capability or has never asked for one. Stamping an
/// <c>Editable</c> flag onto each control was rejected for the same reason: the projection
/// would have to know about the sink, and <c>Project</c> would stop being a pure function of
/// layout plus values.
/// </para>
/// <para>
/// The honest cost: an editable host holds two objects and joins them by field reference name.
/// </para>
/// </remarks>
public sealed class EditCapability
{
    private readonly IChangeSink _sink;
    private readonly ProcessConfiguration? _processConfiguration;
    private readonly WorkItemType _workItemType;

    /// <summary>
    /// Builds a capability over <paramref name="sink"/>.
    /// </summary>
    /// <param name="sink">The destination whose declaration decides what may be edited.</param>
    /// <param name="workItemType">The item's type, used to select transition rules.</param>
    /// <param name="processConfiguration">
    /// Optional process configuration. Without it, <see cref="OfferedStates"/> returns empty and
    /// <see cref="Validate"/> accepts any state move — Twig cannot honestly claim a transition
    /// is illegal when it has no rules to judge by. Absent metadata degrades to "I don't know",
    /// never to a confident refusal.
    /// </param>
    public EditCapability(
        IChangeSink sink,
        WorkItemType workItemType,
        ProcessConfiguration? processConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _workItemType = workItemType;
        _processConfiguration = processConfiguration;
    }

    /// <summary>
    /// The field reference names that accept input — exactly what the sink declared.
    /// </summary>
    /// <remarks>
    /// This is a <i>consequence</i> of the sink's declaration, never a hard-coded list. A host
    /// that keeps its own parallel list of editable fields has built a second answer to
    /// "which fields do we show", which is the defect this whole seam exists to prevent.
    /// </remarks>
    public IReadOnlySet<string> EditableFieldRefs => _sink.PersistableFieldRefs;

    /// <summary>Whether <paramref name="fieldRef"/> accepts input.</summary>
    public bool CanEdit(string fieldRef) =>
        fieldRef is not null && _sink.PersistableFieldRefs.Contains(fieldRef);

    /// <summary>
    /// The states this item may legally move to from <paramref name="fromState"/>, so a host can
    /// offer only legal targets.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>ADVISORY, NOT AUTHORITATIVE. The server is final.</b> Azure DevOps' real
    /// per-process transition graph requires process-admin permission to fetch, and most twig
    /// users are contributors rather than admins. Twig therefore infers legality from the
    /// standard process templates, so the server can refuse a transition offered here. A host
    /// that treats this list as a guarantee will render a legitimate refusal as a bug.
    /// Returns empty when no process configuration was supplied.
    /// </remarks>
    public IReadOnlyList<string> OfferedStates(string fromState)
    {
        if (_processConfiguration is null || string.IsNullOrEmpty(fromState))
            return [];

        if (!_processConfiguration.TypeConfigs.TryGetValue(_workItemType, out var typeConfig))
            return [];

        var offered = new List<string>();
        foreach (var candidate in typeConfig.States)
        {
            if (string.Equals(candidate, fromState, StringComparison.OrdinalIgnoreCase))
                continue;

            if (StateTransitionService.Evaluate(_processConfiguration, _workItemType, fromState, candidate).IsAllowed)
                offered.Add(candidate);
        }

        return offered;
    }

    /// <summary>
    /// Re-validates <paramref name="proposal"/> on the way in, so a host that ignored
    /// <see cref="OfferedStates"/> cannot push an illegal change into the sink.
    /// </summary>
    /// <remarks>
    /// The contract carries three layers, in order: offer-time filter → entry-time validation →
    /// the server is final. This is the second. It is not a substitute for the third.
    /// </remarks>
    public ValidationOutcome Validate(ChangeProposal proposal)
    {
        if (proposal is FieldEdit edit)
        {
            return CanEdit(edit.FieldRef)
                ? new Accepted()
                : new Rejected($"Field '{edit.FieldRef}' is not persistable by this sink.");
        }

        if (proposal is StateMove move)
        {
            foreach (var accompanying in move.Accompanying)
            {
                if (!CanEdit(accompanying.FieldRef))
                {
                    return new Rejected(
                        $"Field '{accompanying.FieldRef}' accompanying the state move is not persistable by this sink.");
                }
            }

            // No process configuration means no basis to refuse. Twig does not invent a refusal
            // it cannot justify; the server remains the authority either way.
            if (_processConfiguration is null)
                return new Accepted();

            var result = StateTransitionService.Evaluate(
                _processConfiguration, _workItemType, move.FromState, move.ToState);

            return result.IsAllowed
                ? new Accepted()
                : new Rejected(
                    $"Transition '{move.FromState}' → '{move.ToState}' is not allowed by the process configuration. " +
                    "Note this check is advisory — the server is the final authority.");
        }

        return new Rejected("Unrecognised change proposal.");
    }
}

/// <summary>The proposal is well-formed and may be submitted.</summary>
public sealed record Accepted;

/// <summary>The proposal was refused at entry, with a host-displayable reason.</summary>
public sealed record Rejected(string Reason);

/// <summary>Outcome of <see cref="EditCapability.Validate"/>.</summary>
public union ValidationOutcome(Accepted, Rejected);
