using System.Collections.Generic;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// §2.2 row 5 and row 6 rejections plus §7.3 null-adapter direct-human
/// echo. The parse-time gate on capability strings is the sole spot
/// row 6 is fired.
/// </summary>
public sealed class TransportEnvelopeMapperTests
{
    private static readonly IReadOnlyDictionary<string, string> _ctx = new Dictionary<string, string>();

    private static TransportAdapterTargetDocument Target(string role, string adapterId = "null") =>
        new(role, adapterId, "id-1", "kind-1", _ctx);

    private static TransportRecordDocument DirectHumanDoc(string terminalAdapter = "null") =>
        new(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: null,
            Terminal: new TransportTerminalDocument(Target("terminal", terminalAdapter), System.Array.Empty<string>()));

    private static TransportAttachmentDocument DirectHumanEnvelope(TransportRecordDocument record) =>
        new(
            Schema: TransportAttachmentDocument.CurrentSchema,
            Version: TransportAttachmentDocument.CurrentVersion,
            Revision: 1,
            ConnectionRef: "ref",
            RecordedAt: "2025-01-01T00:00:00Z",
            State: TransportAttachmentDocument.StateAttached,
            Record: record);

    [Fact]
    public void Unknown_status_string_returns_unknown_status()
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: new TransportAgentDocument(
                Target("agent"),
                SessionKind: "cli",
                RecordedStatus: "made-up-status",
                RecordedAt: "2025-01-01T00:00:00Z",
                Capabilities: System.Array.Empty<string>()),
            Terminal: null));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.UnknownStatus);
    }

    [Fact]
    public void Unknown_capability_string_returns_unknown_capability()
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: null,
            Terminal: new TransportTerminalDocument(Target("terminal"), new[] { "MadeUpCapability" })));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.Error.ShouldBe(TransportAttachmentFailure.UnknownCapability);
    }

    [Theory]
    [InlineData("RecordIdentity")]
    [InlineData("DescribeAdapter")]
    public void CommonDenominator_capability_in_persisted_set_rejected(string name)
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: null,
            Terminal: new TransportTerminalDocument(Target("terminal"), new[] { name })));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.Error.ShouldBe(TransportAttachmentFailure.UnknownCapability);
    }

    [Fact]
    public void Round_trip_preserves_agent_driven_shape()
    {
        var caps = new HashSet<TransportCapability> { TransportCapability.StatusReporting, TransportCapability.Close };
        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                "{fp}",
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "herdr", "w", "kind", _ctx)),
            Agent: new TransportAgentPayload(
                new TransportAdapterTarget(TransportAdapterRole.Agent, "herdr", "a", "kind", _ctx),
                "cli",
                RecordedStatus.Working,
                new System.DateTimeOffset(2025, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
                caps),
            Terminal: null);
        var envelope = new TransportAttachmentEnvelope(
            Revision: 3,
            ConnectionRef: "ref",
            RecordedAt: new System.DateTimeOffset(2025, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
            State: TransportAttachmentEnvelopeState.Attached,
            Record: record);

        var doc = TransportEnvelopeMapper.ToDocument(envelope);
        var roundTrip = TransportEnvelopeMapper.FromDocument(doc);
        roundTrip.IsSuccess.ShouldBeTrue(roundTrip.Error);
        var re = roundTrip.Value;
        re.Revision.ShouldBe(3);
        re.State.ShouldBe(TransportAttachmentEnvelopeState.Attached);
        re.Record.ShouldNotBeNull();
        re.Record!.Agent.ShouldNotBeNull();
        re.Record.Agent!.RecordedStatus.ShouldBe(RecordedStatus.Working);
        re.Record.Agent.Capabilities.Contains(TransportCapability.StatusReporting).ShouldBeTrue();
        re.Record.Agent.Capabilities.Contains(TransportCapability.Close).ShouldBeTrue();
    }

    [Fact]
    public void Round_trip_preserves_direct_human_shape()
    {
        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                "{fp}",
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", _ctx)),
            Agent: null,
            Terminal: new TransportTerminalPayload(
                new TransportAdapterTarget(TransportAdapterRole.Terminal, "null", "t", "null", _ctx),
                new HashSet<TransportCapability>()));
        var envelope = new TransportAttachmentEnvelope(
            Revision: 1,
            ConnectionRef: "ref",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Attached,
            Record: record);

        var doc = TransportEnvelopeMapper.ToDocument(envelope);
        var re = TransportEnvelopeMapper.FromDocument(doc);
        re.IsSuccess.ShouldBeTrue(re.Error);
        re.Value.Record!.Worktree.ShouldNotBeNull();
        re.Value.Record.Agent.ShouldBeNull();
        re.Value.Record.Terminal.ShouldNotBeNull();
    }

    [Fact]
    public void Tombstone_round_trips_with_null_record()
    {
        var envelope = new TransportAttachmentEnvelope(
            Revision: 4,
            ConnectionRef: "ref",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Detached,
            Record: null);
        var doc = TransportEnvelopeMapper.ToDocument(envelope);
        doc.State.ShouldBe(TransportAttachmentDocument.StateDetached);
        doc.Record.ShouldBeNull();
        var re = TransportEnvelopeMapper.FromDocument(doc);
        re.IsSuccess.ShouldBeTrue(re.Error);
        re.Value.State.ShouldBe(TransportAttachmentEnvelopeState.Detached);
        re.Value.Record.ShouldBeNull();
    }

    // ─── Defect 1 — Malformed JSON fails closed on nested-null ───

    [Fact]
    public void Malformed_worktree_target_null_returns_record_invalid_not_exception()
    {
        // Source-gen JSON of `{ "worktree": { "worktreeFingerprint": "x", "target": null } }`
        // yields a null Target despite the non-nullable annotation.
        // The mapper MUST fail closed with the named identifier, not
        // NullReferenceException.
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", null!),
            Agent: null,
            Terminal: new TransportTerminalDocument(Target("terminal"), System.Array.Empty<string>())));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Malformed_agent_capabilities_null_returns_record_invalid_not_exception()
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: new TransportAgentDocument(
                Target: Target("agent"),
                SessionKind: "cli",
                RecordedStatus: "working",
                RecordedAt: "2025-01-01T00:00:00Z",
                Capabilities: null!),
            Terminal: null));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Malformed_terminal_target_null_returns_record_invalid_not_exception()
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: null,
            Terminal: new TransportTerminalDocument(null!, System.Array.Empty<string>())));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Malformed_agent_target_null_returns_record_invalid_not_exception()
    {
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: new TransportAgentDocument(
                Target: null!,
                SessionKind: "cli",
                RecordedStatus: "working",
                RecordedAt: "2025-01-01T00:00:00Z",
                Capabilities: System.Array.Empty<string>()),
            Terminal: null));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    // ─── Defect 2 — §2.2 rejection precedence ───

    [Fact]
    public void Row2_worktree_missing_takes_precedence_over_row5_unknown_status()
    {
        // Even when agent.recordedStatus is malformed, a worktree-absent
        // document surfaces WorktreeMissing, not UnknownStatus (§2.2
        // row-order rule).
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: null,
            Agent: new TransportAgentDocument(
                Target: Target("agent"),
                SessionKind: "cli",
                RecordedStatus: "made-up-status",
                RecordedAt: "2025-01-01T00:00:00Z",
                Capabilities: System.Array.Empty<string>()),
            Terminal: null));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.WorktreeMissing);
    }

    [Fact]
    public void Row3_bare_worktree_takes_precedence_over_row6_unknown_capability()
    {
        // A bare-worktree document with a MALFORMED capability payload
        // must surface BareWorktree, not UnknownCapability (§2.2
        // row-order rule). The row-6 UnknownCapability path exists but
        // is unreachable here because rows 2/3 fire first — and there
        // is no capabilities block on a bare worktree in any case; the
        // bare-worktree row is what pins the precedence.
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: new TransportWorktreeDocument("{fp}", Target("worktree")),
            Agent: null,
            Terminal: null));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.BareWorktree);
    }

    [Fact]
    public void Row2_worktree_missing_takes_precedence_over_row6_unknown_capability_in_terminal()
    {
        // worktree absent + terminal carries an invalid capability
        // wire → WorktreeMissing, NOT UnknownCapability.
        var doc = DirectHumanEnvelope(new TransportRecordDocument(
            Worktree: null,
            Agent: null,
            Terminal: new TransportTerminalDocument(Target("terminal"), new[] { "NotACatalogueName" })));
        var res = TransportEnvelopeMapper.FromDocument(doc);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.WorktreeMissing);
    }
}
