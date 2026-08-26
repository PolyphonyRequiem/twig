using System.Collections.Generic;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// Fail-closed contract tests for
/// <see cref="TransportShapeValidator"/>. Each case exercises one §2.2
/// row (identifier), fixed evaluation order, and non-overlap
/// property. Serves as the "invalid combinations rejected" leg of the
/// AB#745 §12.1 acceptance criteria.
/// </summary>
public sealed class TransportShapeValidatorTests
{
    private static readonly IReadOnlyDictionary<string, string> _emptyCtx = new Dictionary<string, string>();
    private static readonly IReadOnlySet<TransportCapability> _noCaps = new HashSet<TransportCapability>();

    private static TransportAdapterTarget WorktreeTarget(string adapterId = "null") =>
        new(TransportAdapterRole.Worktree, adapterId, "wt-1", "null", _emptyCtx);

    private static TransportAdapterTarget AgentTarget(string adapterId = "herdr") =>
        new(TransportAdapterRole.Agent, adapterId, "a-1", "herdr-pane", _emptyCtx);

    private static TransportAdapterTarget TerminalTarget(string adapterId = "null") =>
        new(TransportAdapterRole.Terminal, adapterId, "t-1", "null", _emptyCtx);

    private static TransportWorktreePayload Worktree(string adapterId = "null") =>
        new("{\"gitCommonDir\":\"a\",\"worktreeGitDir\":\"a\",\"worktreeRoot\":\"a\"}", WorktreeTarget(adapterId));

    private static TransportAgentPayload Agent(RecordedStatus status = RecordedStatus.Working) =>
        new(AgentTarget(), "cli", status, System.DateTimeOffset.UnixEpoch, _noCaps);

    private static TransportTerminalPayload Terminal(string adapterId = "null") =>
        new(TerminalTarget(adapterId), _noCaps);

    [Fact]
    public void DirectHuman_shape_is_accepted()
    {
        var record = new TransportAttachmentRecord(Worktree(), Agent: null, Terminal: Terminal());
        var result = TransportShapeValidator.ValidateRecord(record);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void AgentDriven_with_terminal_is_accepted()
    {
        var record = new TransportAttachmentRecord(Worktree(), Agent(), Terminal());
        var result = TransportShapeValidator.ValidateRecord(record);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void AgentDriven_without_terminal_is_accepted()
    {
        var record = new TransportAttachmentRecord(Worktree(), Agent(), Terminal: null);
        var result = TransportShapeValidator.ValidateRecord(record);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Missing_worktree_returns_worktree_missing()
    {
        var record = new TransportAttachmentRecord(Worktree: null, Agent(), Terminal());
        var result = TransportShapeValidator.ValidateRecord(record);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.WorktreeMissing);
    }

    [Fact]
    public void Bare_worktree_returns_bare_worktree()
    {
        var record = new TransportAttachmentRecord(Worktree(), Agent: null, Terminal: null);
        var result = TransportShapeValidator.ValidateRecord(record);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.BareWorktree);
    }

    [Fact]
    public void Row_ordering_worktree_missing_fires_before_bare_worktree()
    {
        // Both rows 2 and 3 could apply if we tested (worktree null + agent null + terminal null);
        // fixed order says row 2 wins.
        var record = new TransportAttachmentRecord(Worktree: null, Agent: null, Terminal: null);
        var result = TransportShapeValidator.ValidateRecord(record);
        result.Error.ShouldBe(TransportAttachmentFailure.WorktreeMissing);
    }

    [Fact]
    public void Envelope_detached_state_requires_null_record()
    {
        var envelope = new TransportAttachmentEnvelope(
            Revision: 2,
            ConnectionRef: "x",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Detached,
            Record: new TransportAttachmentRecord(Worktree(), Agent: null, Terminal: Terminal()));
        var result = TransportShapeValidator.Validate(envelope);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Envelope_attached_state_requires_present_record()
    {
        var envelope = new TransportAttachmentEnvelope(
            Revision: 1,
            ConnectionRef: "x",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Attached,
            Record: null);
        var result = TransportShapeValidator.Validate(envelope);
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Envelope_tombstone_skips_shape_rows()
    {
        var envelope = new TransportAttachmentEnvelope(
            Revision: 5,
            ConnectionRef: "x",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Detached,
            Record: null);
        var result = TransportShapeValidator.Validate(envelope);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Envelope_revision_below_one_is_record_invalid()
    {
        var envelope = new TransportAttachmentEnvelope(
            Revision: 0,
            ConnectionRef: "x",
            RecordedAt: System.DateTimeOffset.UnixEpoch,
            State: TransportAttachmentEnvelopeState.Attached,
            Record: new TransportAttachmentRecord(Worktree(), Agent(), Terminal()));
        var result = TransportShapeValidator.Validate(envelope);
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }
}
