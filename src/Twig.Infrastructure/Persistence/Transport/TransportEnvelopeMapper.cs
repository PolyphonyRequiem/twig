using System.Collections.Generic;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Bidirectional translator between the persisted
/// <see cref="TransportAttachmentDocument"/> JSON shape and the
/// in-memory <see cref="TransportAttachmentEnvelope"/> +
/// <see cref="TransportAttachmentRecord"/> model. Owns the §2.2 row-5
/// (status) and row-6 (capabilities) rejections at parse time — a
/// persisted string outside the fixed catalogue never becomes a live
/// enum value; the store's validator therefore only has to decide
/// shape-row combinations.
/// </summary>
internal static class TransportEnvelopeMapper
{
    /// <summary>Convert a parsed on-disk document to the in-memory
    /// envelope. Envelope-level errors (unknown schema/version, bad
    /// state string, non-parseable timestamps) surface as row 1
    /// <see cref="TransportAttachmentFailure.RecordInvalid"/>. §2.2
    /// row-5/6 rejections surface with their own identifiers so the
    /// shape validator's failure catalog matches §11 verbatim.</summary>
    public static Result<TransportAttachmentEnvelope> FromDocument(TransportAttachmentDocument doc)
    {
        if (!string.Equals(doc.Schema, TransportAttachmentDocument.CurrentSchema, System.StringComparison.Ordinal))
            return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
        if (doc.Version != TransportAttachmentDocument.CurrentVersion)
            return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
        if (doc.Revision < 1)
            return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
        if (string.IsNullOrEmpty(doc.ConnectionRef))
            return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);

        TransportAttachmentEnvelopeState state;
        switch (doc.State)
        {
            case TransportAttachmentDocument.StateAttached: state = TransportAttachmentEnvelopeState.Attached; break;
            case TransportAttachmentDocument.StateDetached: state = TransportAttachmentEnvelopeState.Detached; break;
            default: return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
        }

        if (!TryParseUtc(doc.RecordedAt, out var recordedAt))
            return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);

        TransportAttachmentRecord? record = null;
        if (state == TransportAttachmentEnvelopeState.Attached)
        {
            if (doc.Record is null)
                return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
            var recordResult = FromRecordDocument(doc.Record);
            if (!recordResult.IsSuccess)
                return Result.Fail<TransportAttachmentEnvelope>(recordResult.Error);
            record = recordResult.Value;
        }
        else
        {
            // Detached: record MUST be null on disk.
            if (doc.Record is not null)
                return Result.Fail<TransportAttachmentEnvelope>(TransportAttachmentFailure.RecordInvalid);
        }

        return Result.Ok(new TransportAttachmentEnvelope(
            Revision: doc.Revision,
            ConnectionRef: doc.ConnectionRef,
            RecordedAt: recordedAt,
            State: state,
            Record: record));
    }

    /// <summary>Serialize the in-memory envelope back to the wire
    /// document. Every enum returns its canonical §2.1/§3.3/§4.1 wire
    /// string.</summary>
    public static TransportAttachmentDocument ToDocument(TransportAttachmentEnvelope envelope)
    {
        return new TransportAttachmentDocument(
            Schema: TransportAttachmentDocument.CurrentSchema,
            Version: TransportAttachmentDocument.CurrentVersion,
            Revision: envelope.Revision,
            ConnectionRef: envelope.ConnectionRef,
            RecordedAt: envelope.RecordedAt.ToUniversalTime().ToString("o"),
            State: envelope.State == TransportAttachmentEnvelopeState.Attached
                ? TransportAttachmentDocument.StateAttached
                : TransportAttachmentDocument.StateDetached,
            Record: envelope.Record is null ? null : ToRecordDocument(envelope.Record));
    }

    private static Result<TransportAttachmentRecord> FromRecordDocument(TransportRecordDocument doc)
    {
        TransportWorktreePayload? worktree = null;
        if (doc.Worktree is { } w)
        {
            if (string.IsNullOrEmpty(w.WorktreeFingerprint))
                return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.RecordInvalid);
            var targetResult = FromTargetDocument(w.Target, expectedRole: TransportAdapterRole.Worktree);
            if (!targetResult.IsSuccess)
                return Result.Fail<TransportAttachmentRecord>(targetResult.Error);
            worktree = new TransportWorktreePayload(w.WorktreeFingerprint, targetResult.Value);
        }

        TransportAgentPayload? agent = null;
        if (doc.Agent is { } a)
        {
            var targetResult = FromTargetDocument(a.Target, expectedRole: TransportAdapterRole.Agent);
            if (!targetResult.IsSuccess)
                return Result.Fail<TransportAttachmentRecord>(targetResult.Error);
            if (string.IsNullOrEmpty(a.SessionKind))
                return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.RecordInvalid);
            if (!RecordedStatusExtensions.TryParse(a.RecordedStatus, out var status))
                return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.UnknownStatus);
            if (!TryParseUtc(a.RecordedAt, out var recordedAt))
                return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.RecordInvalid);
            var capsResult = FromCapabilityWireStrings(a.Capabilities);
            if (!capsResult.IsSuccess)
                return Result.Fail<TransportAttachmentRecord>(capsResult.Error);
            agent = new TransportAgentPayload(
                Target: targetResult.Value,
                SessionKind: a.SessionKind,
                RecordedStatus: status,
                RecordedAt: recordedAt,
                Capabilities: capsResult.Value);
        }

        TransportTerminalPayload? terminal = null;
        if (doc.Terminal is { } t)
        {
            var targetResult = FromTargetDocument(t.Target, expectedRole: TransportAdapterRole.Terminal);
            if (!targetResult.IsSuccess)
                return Result.Fail<TransportAttachmentRecord>(targetResult.Error);
            var capsResult = FromCapabilityWireStrings(t.Capabilities);
            if (!capsResult.IsSuccess)
                return Result.Fail<TransportAttachmentRecord>(capsResult.Error);
            terminal = new TransportTerminalPayload(targetResult.Value, capsResult.Value);
        }

        return Result.Ok(new TransportAttachmentRecord(worktree, agent, terminal));
    }

    private static TransportRecordDocument ToRecordDocument(TransportAttachmentRecord record)
    {
        return new TransportRecordDocument(
            Worktree: record.Worktree is null ? null : new TransportWorktreeDocument(
                WorktreeFingerprint: record.Worktree.WorktreeFingerprint,
                Target: ToTargetDocument(record.Worktree.Target)),
            Agent: record.Agent is null ? null : new TransportAgentDocument(
                Target: ToTargetDocument(record.Agent.Target),
                SessionKind: record.Agent.SessionKind,
                RecordedStatus: record.Agent.RecordedStatus.ToWire(),
                RecordedAt: record.Agent.RecordedAt.ToUniversalTime().ToString("o"),
                Capabilities: ToCapabilityWireStrings(record.Agent.Capabilities)),
            Terminal: record.Terminal is null ? null : new TransportTerminalDocument(
                Target: ToTargetDocument(record.Terminal.Target),
                Capabilities: ToCapabilityWireStrings(record.Terminal.Capabilities)));
    }

    private static Result<TransportAdapterTarget> FromTargetDocument(
        TransportAdapterTargetDocument doc,
        TransportAdapterRole expectedRole)
    {
        if (string.IsNullOrEmpty(doc.AdapterId))
            return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
        if (string.IsNullOrEmpty(doc.HostAttachmentId))
            return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
        if (string.IsNullOrEmpty(doc.HostAttachmentIdKind))
            return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
        if (!TryParseRole(doc.Role, out var role) || role != expectedRole)
            return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);

        return Result.Ok(new TransportAdapterTarget(
            Role: role,
            AdapterId: doc.AdapterId,
            HostAttachmentId: doc.HostAttachmentId,
            HostAttachmentIdKind: doc.HostAttachmentIdKind,
            AdapterContext: doc.AdapterContext ?? new Dictionary<string, string>()));
    }

    private static TransportAdapterTargetDocument ToTargetDocument(TransportAdapterTarget target)
    {
        return new TransportAdapterTargetDocument(
            Role: RoleToWire(target.Role),
            AdapterId: target.AdapterId,
            HostAttachmentId: target.HostAttachmentId,
            HostAttachmentIdKind: target.HostAttachmentIdKind,
            AdapterContext: target.AdapterContext);
    }

    /// <summary>§2.2 row 6 gatekeeper. A common-denominator name in a
    /// persisted set (<see cref="TransportCapabilityExtensions.RecordIdentity"/>,
    /// <see cref="TransportCapabilityExtensions.DescribeAdapter"/>) OR
    /// an unknown string BOTH raise
    /// <see cref="TransportAttachmentFailure.UnknownCapability"/>. This
    /// is the sole spot the row-6 rejection is fired; downstream code
    /// only ever handles the parsed <see cref="TransportCapability"/>
    /// enum values.</summary>
    private static Result<IReadOnlySet<TransportCapability>> FromCapabilityWireStrings(
        IReadOnlyList<string> wireStrings)
    {
        var set = new HashSet<TransportCapability>();
        foreach (var wire in wireStrings)
        {
            if (string.IsNullOrEmpty(wire))
                return Result.Fail<IReadOnlySet<TransportCapability>>(TransportAttachmentFailure.UnknownCapability);
            if (TransportCapabilityExtensions.IsCommonDenominator(wire))
                return Result.Fail<IReadOnlySet<TransportCapability>>(TransportAttachmentFailure.UnknownCapability);
            if (!TransportCapabilityExtensions.TryParse(wire, out var capability))
                return Result.Fail<IReadOnlySet<TransportCapability>>(TransportAttachmentFailure.UnknownCapability);
            set.Add(capability);
        }
        return Result.Ok<IReadOnlySet<TransportCapability>>(set);
    }

    private static IReadOnlyList<string> ToCapabilityWireStrings(IReadOnlySet<TransportCapability> caps)
    {
        var list = new List<string>(caps.Count);
        // Deterministic ordering by enum value so on-disk bytes are
        // stable across re-serializations of the same in-memory value.
        foreach (var capability in System.Linq.Enumerable.OrderBy(caps, c => (int)c))
            list.Add(capability.ToWire());
        return list;
    }

    private const string RoleWorktree = "worktree";
    private const string RoleAgent = "agent";
    private const string RoleTerminal = "terminal";

    private static string RoleToWire(TransportAdapterRole role) => role switch
    {
        TransportAdapterRole.Worktree => RoleWorktree,
        TransportAdapterRole.Agent => RoleAgent,
        TransportAdapterRole.Terminal => RoleTerminal,
        _ => throw new System.ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static bool TryParseRole(string wire, out TransportAdapterRole role)
    {
        switch (wire)
        {
            case RoleWorktree: role = TransportAdapterRole.Worktree; return true;
            case RoleAgent: role = TransportAdapterRole.Agent; return true;
            case RoleTerminal: role = TransportAdapterRole.Terminal; return true;
            default: role = default; return false;
        }
    }

    private static bool TryParseUtc(string wire, out System.DateTimeOffset value)
    {
        return System.DateTimeOffset.TryParse(
            wire,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out value);
    }
}
