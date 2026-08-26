using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// §8.4 fail-closed contract tests for
/// <see cref="WorktreeLocalTransportAttachmentStore"/>. Matches the
/// AB#736 <c>WorktreeLocalAttachmentStoreTests</c> shape so an
/// operator inspecting one attachment store's tests sees the same
/// scaffolding for the other. Tests skip when git is unavailable —
/// the store's read path resolves the worktree fingerprint from a
/// live rev-parse (§2.1 / §8.4).
/// </summary>
public sealed class WorktreeLocalTransportAttachmentStoreTests : System.IDisposable
{
    private readonly string _workDir;
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly bool _gitAvailable;

    public WorktreeLocalTransportAttachmentStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "twig-transport-tests-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var twigDir = Path.Combine(_workDir, ".twig");
        _paths = new TwigPaths(
            twigDir: twigDir,
            configPath: Path.Combine(twigDir, "config"),
            dbPath: Path.Combine(twigDir, "twig.db"),
            startDir: _workDir);
        _config = new TwigConfiguration
        {
            Organization = "fixture-org",
            Project = "fixture-project",
        };
        File.WriteAllText(Path.Combine(_workDir, "twig.json"), "{\n}\n");
        _gitAvailable = TryInitGit(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
    }

    private static bool TryInitGit(string dir)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private WorktreeLocalTransportAttachmentStore NewStore() => new(_paths, _config, TimeProvider.System);

    private static TransportAttachmentRecord AgentDrivenRecord(string worktreeFp)
    {
        var ctx = new Dictionary<string, string>();
        return new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                worktreeFp,
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", ctx)),
            Agent: new TransportAgentPayload(
                new TransportAdapterTarget(TransportAdapterRole.Agent, "null", "a", "null", ctx),
                SessionKind: "cli",
                RecordedStatus: RecordedStatus.Working,
                RecordedAt: System.DateTimeOffset.UnixEpoch,
                Capabilities: new HashSet<TransportCapability>()),
            Terminal: null);
    }

    private static TransportAttachmentRecord DirectHumanRecord(string worktreeFp)
    {
        var ctx = new Dictionary<string, string>();
        return new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                worktreeFp,
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", ctx)),
            Agent: null,
            Terminal: new TransportTerminalPayload(
                new TransportAdapterTarget(TransportAdapterRole.Terminal, "null", "t", "null", ctx),
                new HashSet<TransportCapability>()));
    }

    private string LiveWorktreeFingerprint()
    {
        if (!WorktreeAnchorDetector.TryDetect(_workDir, out var anchor, out _))
            return string.Empty;
        return Twig.Infrastructure.Persistence.WorktreeFingerprintProvider.CanonicalJson(anchor);
    }

    [Fact]
    public async Task Read_on_never_existent_file_returns_null_envelope_and_revision_zero()
    {
        var store = NewStore();
        var res = await store.ReadWithRevisionAsync();
        res.IsSuccess.ShouldBeTrue(res.Error);
        res.Value.Envelope.ShouldBeNull();
        res.Value.Revision.ShouldBe(0);
    }

    [Fact]
    public async Task First_attach_writes_revision_one()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();
        var record = AgentDrivenRecord(fp);
        var write = await store.WriteAsync(record, expectedRevision: 0);
        write.IsSuccess.ShouldBeTrue(write.Error);
        write.Value.WrittenRevision.ShouldBe(1);

        var read = await store.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.Envelope.ShouldNotBeNull();
        read.Value.Envelope!.State.ShouldBe(TransportAttachmentEnvelopeState.Attached);
        read.Value.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Write_with_stale_expected_revision_returns_version_mismatch()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();
        var write = await store.WriteAsync(AgentDrivenRecord(fp), expectedRevision: 0);
        write.IsSuccess.ShouldBeTrue(write.Error);

        // Second write with the stale revision zero (should be 1 now).
        var stale = await store.WriteAsync(AgentDrivenRecord(fp), expectedRevision: 0);
        stale.IsSuccess.ShouldBeFalse();
        stale.Error.ShouldBe(TransportAttachmentFailure.VersionMismatch);
    }

    [Fact]
    public async Task Detach_writes_tombstone_and_advances_revision()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();
        var write = await store.WriteAsync(AgentDrivenRecord(fp), expectedRevision: 0);
        write.IsSuccess.ShouldBeTrue();

        var detach = await store.DetachAsync(expectedRevision: 1);
        detach.IsSuccess.ShouldBeTrue(detach.Error);
        detach.Value.WrittenRevision.ShouldBe(2);

        var read = await store.ReadWithRevisionAsync();
        read.Value.Envelope.ShouldNotBeNull();
        read.Value.Envelope!.State.ShouldBe(TransportAttachmentEnvelopeState.Detached);
        read.Value.Envelope.Record.ShouldBeNull();
        read.Value.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Reattach_after_detach_advances_revision_not_rewinds_it()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();
        (await store.WriteAsync(AgentDrivenRecord(fp), 0)).IsSuccess.ShouldBeTrue();
        (await store.DetachAsync(1)).IsSuccess.ShouldBeTrue();

        // ABA safeguard: reattach post-tombstone MUST NOT rewind to
        // revision 1. §8.2 preserves the CAS token across detach +
        // reattach — the tombstone kept revision 2, so the reattach
        // becomes revision 3.
        var reattach = await store.WriteAsync(AgentDrivenRecord(fp), expectedRevision: 2);
        reattach.IsSuccess.ShouldBeTrue(reattach.Error);
        reattach.Value.WrittenRevision.ShouldBe(3);
    }

    [Fact]
    public async Task Detach_on_never_existent_file_is_no_op_with_expected_revision_zero()
    {
        // No git required — the never-existent path does not touch
        // the fingerprint check.
        var store = NewStore();
        var detach = await store.DetachAsync(expectedRevision: 0);
        detach.IsSuccess.ShouldBeTrue(detach.Error);
        detach.Value.WrittenRevision.ShouldBe(0);

        var read = await store.ReadWithRevisionAsync();
        read.Value.Envelope.ShouldBeNull();
    }

    [Fact]
    public async Task Close_on_never_existent_file_is_no_op_with_expected_revision_zero()
    {
        var store = NewStore();
        var close = await store.CloseAsync(expectedRevision: 0);
        close.IsSuccess.ShouldBeTrue(close.Error);
        close.Value.WrittenRevision.ShouldBe(0);
    }

    [Fact]
    public async Task Write_of_invalid_shape_returns_shape_failure_without_touching_disk()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var ctx = new Dictionary<string, string>();
        // Bare worktree (row 3) — worktree only, no agent or terminal.
        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload("{fp}",
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", ctx)),
            Agent: null,
            Terminal: null);
        var res = await store.WriteAsync(record, expectedRevision: 0);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.BareWorktree);

        var transportPath = Path.Combine(_paths.TwigDir, WorktreeLocalTransportAttachmentStore.TransportFileName);
        File.Exists(transportPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DirectHuman_shape_round_trips_via_store()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();
        var write = await store.WriteAsync(DirectHumanRecord(fp), expectedRevision: 0);
        write.IsSuccess.ShouldBeTrue(write.Error);

        var read = await store.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.Envelope!.Record!.Agent.ShouldBeNull();
        read.Value.Envelope.Record.Terminal.ShouldNotBeNull();
    }

    [Fact]
    public async Task NullAdapter_direct_human_shape_persists_and_round_trips()
    {
        if (!_gitAvailable) return;
        var store = NewStore();
        var fp = LiveWorktreeFingerprint();

        var nullAdapter = new NullTransportAdapter();
        var ctx = new Dictionary<string, string>();
        var identity = nullAdapter.RecordIdentity(new RecordIdentityRequest(
            WorktreeFingerprint: fp,
            WorktreeTarget: new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", ctx),
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: new TransportAdapterTarget(TransportAdapterRole.Terminal, "null", "t", "null", ctx),
            AgentCapabilities: new HashSet<TransportCapability>(),
            TerminalCapabilities: new HashSet<TransportCapability>(),
            AgentRecordedStatus: RecordedStatus.Unobservable,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch));
        identity.IsSuccess.ShouldBeTrue(identity.Error);

        var write = await store.WriteAsync(identity.Value, expectedRevision: 0);
        write.IsSuccess.ShouldBeTrue(write.Error);

        var read = await store.ReadWithRevisionAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.Envelope!.Record!.Worktree!.Target.AdapterId.ShouldBe("null");
        read.Value.Envelope.Record.Terminal!.Target.AdapterId.ShouldBe("null");
        read.Value.Envelope.Record.Agent.ShouldBeNull();
    }
}
