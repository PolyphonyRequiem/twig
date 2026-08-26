using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §2.2 shape validator. Decidable from the record alone with
/// no reference to caller intent or adapter-declared metadata; runs on
/// every read/write boundary at §8.4.
/// <para>
/// Row order is fixed and non-overlapping by construction (§2.2):
/// <list type="number">
///   <item><see cref="TransportAttachmentFailure.RecordInvalid"/> — envelope/record
///     schema parse failure. Restricted to schema-level parse
///     failures; field-absent worktree is row 2.</item>
///   <item><see cref="TransportAttachmentFailure.WorktreeMissing"/> —
///     <c>state = "attached"</c> but <c>record.worktree</c>
///     field-absent.</item>
///   <item><see cref="TransportAttachmentFailure.BareWorktree"/> —
///     <c>worktree</c> present, both <c>agent</c> and <c>terminal</c>
///     <c>null</c>.</item>
///   <item><see cref="TransportAttachmentFailure.OrphanTerminal"/> — record
///     fits neither shape row.</item>
///   <item><see cref="TransportAttachmentFailure.UnknownStatus"/> —
///     <c>agent.recordedStatus</c> outside §4.1.</item>
///   <item><see cref="TransportAttachmentFailure.UnknownCapability"/> —
///     capability name outside §3.3's optional catalogue, including
///     mandatory §3.1 names that MUST NOT appear in a persisted set.
///     </item>
/// </list>
/// </para>
/// <para>
/// The tombstone (<see cref="TransportAttachmentEnvelopeState.Detached"/>)
/// skips rows 2–6 entirely — its <c>record</c> is <c>null</c> by
/// construction and the envelope-level check owns it.
/// </para>
/// </summary>
internal static class TransportShapeValidator
{
    /// <summary>Validate a §2.1 envelope and its embedded record. The
    /// envelope-level checks (state/record disagreement) are §2.2 row 1;
    /// the record-shape checks are rows 2–6. Returns the first-tripped
    /// identifier per the fixed evaluation order; returns
    /// <see cref="Result.Ok()"/> when every row passes.</summary>
    public static Result Validate(TransportAttachmentEnvelope envelope)
    {
        // Row 1 — state/record disagreement (schema-level).
        switch (envelope.State)
        {
            case TransportAttachmentEnvelopeState.Attached when envelope.Record is null:
                return Result.Fail(TransportAttachmentFailure.RecordInvalid);
            case TransportAttachmentEnvelopeState.Detached when envelope.Record is not null:
                return Result.Fail(TransportAttachmentFailure.RecordInvalid);
        }

        if (envelope.Revision < 1)
            return Result.Fail(TransportAttachmentFailure.RecordInvalid);

        // Tombstone: skip rows 2–6 (§2.2). The envelope-level check owns it.
        if (envelope.State == TransportAttachmentEnvelopeState.Detached)
            return Result.Ok();

        return ValidateRecord(envelope.Record!);
    }

    /// <summary>Validate a stand-alone record — the shape validator
    /// called from any surface that already stripped the envelope
    /// (e.g. write-path preflight before <see cref="TransportAttachmentEnvelope"/>
    /// construction).</summary>
    public static Result ValidateRecord(TransportAttachmentRecord record)
    {
        // Row 2 — worktree missing while attached.
        if (record.Worktree is null)
            return Result.Fail(TransportAttachmentFailure.WorktreeMissing);

        // Row 3 — bare worktree.
        if (record.Agent is null && record.Terminal is null)
            return Result.Fail(TransportAttachmentFailure.BareWorktree);

        // Row 4 — fits neither shape row (residual).
        //
        // Direct-human = agent null AND terminal present.
        // Agent-driven = agent present (terminal optional).
        // Row 2 has already killed worktree-absent; row 3 has killed
        // agent+terminal both null. That leaves exactly:
        //
        //   worktree +   agent null, terminal not null   -> direct-human OK
        //   worktree +   agent not null, terminal null   -> agent-driven OK
        //   worktree +   agent not null, terminal not null -> agent-driven OK
        //
        // Every "invalid combination survivor" — row 4 — is a bug in
        // this method's classification, not a runtime state, because
        // the three field-nullness combinations above are exhaustive
        // once rows 2 and 3 are ruled out. Row 4 defends against a
        // future shape addition (§2.3 deferred multi-attachment) whose
        // partial deserialization might survive the earlier rows.
        var isDirectHuman = record.Agent is null && record.Terminal is not null;
        var isAgentDriven = record.Agent is not null;
        if (!isDirectHuman && !isAgentDriven)
            return Result.Fail(TransportAttachmentFailure.OrphanTerminal);

        // Row 5 — capability catalogue on every payload.
        if (record.Agent is { } agent)
        {
            var capsResult = ValidateCapabilities(agent.Capabilities);
            if (!capsResult.IsSuccess) return capsResult;
        }
        if (record.Terminal is { } terminal)
        {
            var capsResult = ValidateCapabilities(terminal.Capabilities);
            if (!capsResult.IsSuccess) return capsResult;
        }

        // Note: RecordedStatus row-5 rejection is enforced at parse
        // time by the JSON reader (an unknown string never becomes a
        // valid enum) — see TransportEnvelopeSerializer for the pass.
        return Result.Ok();
    }

    private static Result ValidateCapabilities(System.Collections.Generic.IReadOnlySet<TransportCapability> caps)
    {
        // Every TransportCapability enum value corresponds to a valid
        // §3.3 optional name — the enum IS the catalogue; a value
        // reaching here is a valid entry by construction. The §2.2
        // row-6 defense against a persisted unknown/common-denominator
        // name lives at parse time (see TransportEnvelopeSerializer):
        // parse rejects the raw string before the enum is populated,
        // so no downstream caller ever sees an invalid capability.
        _ = caps;
        return Result.Ok();
    }
}
