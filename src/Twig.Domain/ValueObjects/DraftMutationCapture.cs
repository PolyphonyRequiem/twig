using System.Security.Cryptography;
using System.Text;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Projections;
using CanonicalChangeProposal = Twig.Domain.Services.ChangeProposals.ChangeProposal;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Native authority context captured with a mutation. It is provenance, not a grant
/// to perform the operation.
/// </summary>
internal sealed record DraftAuthorityContext(
    string BindingId,
    long BindingRevision,
    string IdentityId,
    long SelectionRevision,
    string? StorageGeneration);

/// <summary>An immutable exact effect set bound to a canonical native proposal.</summary>
internal sealed class DraftMutationCapture
{
    private DraftMutationCapture(
        QualifiedWorkItemKey itemKey,
        int expectedRevision,
        long capturedDraftGeneration,
        IReadOnlyList<DraftFieldChange> effects,
        string digest,
        DraftAuthorityContext authority)
    {
        ItemKey = itemKey;
        ExpectedRevision = expectedRevision;
        CapturedDraftGeneration = capturedDraftGeneration;
        Effects = Array.AsReadOnly(effects.ToArray());
        Digest = digest;
        Authority = authority;
    }

    public QualifiedWorkItemKey ItemKey { get; }
    public int ExpectedRevision { get; }
    public long CapturedDraftGeneration { get; }
    public IReadOnlyList<DraftFieldChange> Effects { get; }
    public string Digest { get; }
    public DraftAuthorityContext Authority { get; }

    /// <summary>
    /// Captures only a parsed, canonical native proposal that exactly represents this
    /// non-conflicted draft. The capture does not grant authorization.
    /// </summary>
    public static DraftMutationCapture FromCanonicalProposal(
        RecoverableWorkItemDraft draft,
        CanonicalChangeProposal proposal,
        DraftAuthorityContext authority,
        EditCapability capability)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(capability);
        if (authority.BindingRevision < 0 || authority.SelectionRevision < 0)
            throw new InvalidOperationException("The authority context contains an invalid native revision.");
        if (draft.HasConflicts)
            throw new InvalidOperationException("A conflicted draft cannot be captured.");
        if (draft.Changes.Count == 0)
            throw new InvalidOperationException("An empty draft cannot be captured.");
        if (!string.Equals(draft.ItemKey.BindingId, authority.BindingId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(authority.IdentityId))
            throw new InvalidOperationException("The authority context does not match the draft's qualified binding.");
        if (string.IsNullOrEmpty(proposal.Digest) || proposal.Digest.Length != 64
            || proposal.Digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("The native proposal digest is not canonical lowercase SHA-256 hex.");
        if (string.IsNullOrEmpty(proposal.CanonicalJson)
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(proposal.CanonicalJson))),
                proposal.Digest,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The native proposal digest does not match its canonical JSON.");
        if (proposal.Definition is null || proposal.Definition.Operations.Count != 1
            || proposal.Definition.Operations[0] is not BatchOperation operation)
            throw new InvalidOperationException("A draft capture requires exactly one native batch operation.");
        if (operation.WorkItemId != draft.ItemKey.WorkItemId
            || operation.ExpectedRevision != draft.BaselineRevision)
            throw new InvalidOperationException("The native proposal target or revision does not match the draft.");
        if (operation.Fields.Count != draft.Changes.Count)
            throw new InvalidOperationException("The native proposal effects do not exactly match the draft.");

        var proposed = operation.Fields.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var change in draft.Changes)
        {
            if (!proposed.TryGetValue(change.FieldRef, out var value)
                || !string.Equals(change.ProposedValue, value, StringComparison.Ordinal))
                throw new InvalidOperationException("The native proposal effects do not exactly match the draft.");
        }
        ValidateCapability(draft.Changes, capability);

        return new DraftMutationCapture(
            draft.ItemKey, draft.BaselineRevision, draft.Generation,
            draft.Changes, proposal.Digest, authority);
    }

    /// <summary>Returns false when any captured draft, observation, identity, or authority fact changed.</summary>
    public bool IsCurrentFor(
        RecoverableWorkItemDraft draft,
        int currentRevision,
        DraftAuthorityContext currentAuthority)
        => draft is not null
            && currentAuthority is not null
            && ItemKey == draft.ItemKey
            && ExpectedRevision == currentRevision
            && ExpectedRevision == draft.BaselineRevision
            && draft.ObservedRevision == draft.BaselineRevision
            && CapturedDraftGeneration == draft.Generation
            && !draft.HasConflicts
            && Authority == currentAuthority;

    private static void ValidateCapability(
        IReadOnlyList<DraftFieldChange> changes,
        EditCapability capability)
    {
        var stateChange = changes.FirstOrDefault(change =>
            string.Equals(change.FieldRef, "System.State", StringComparison.OrdinalIgnoreCase));
        if (stateChange is null)
        {
            foreach (var change in changes)
            {
                EnsureAccepted(capability.Validate(
                    new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue)));
            }
            return;
        }

        if (stateChange.ProposedValue is null)
            throw new InvalidOperationException("A state transition cannot clear System.State.");
        var accompanying = changes
            .Where(change => !ReferenceEquals(change, stateChange))
            .Select(change => new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue))
            .ToArray();
        EnsureAccepted(capability.Validate(new StateMove(
            stateChange.BaselineValue ?? string.Empty,
            stateChange.ProposedValue,
            accompanying)));
    }

    private static void EnsureAccepted(ValidationOutcome outcome)
    {
        if (outcome is Rejected rejected)
            throw new InvalidOperationException($"The native edit capability refused the draft: {rejected.Reason}");
        if (outcome is not Accepted)
            throw new InvalidOperationException("The native edit capability returned an unsupported result.");
    }
}
