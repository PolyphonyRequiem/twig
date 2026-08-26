using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Twig.Domain.Common;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// §3.2 dispatch and null-adapter conformance. Verifies the
/// absent-capability degradation table row-by-row for the null
/// adapter, verifies §7.3's "unregistered adapter does NOT silently
/// fall through to null" rule, verifies §5.2 folds adapter exceptions
/// into <c>transport-probe-adapter-failed</c>, and verifies §6.1/§6.2
/// tombstone coordination.
/// </summary>
public sealed class TransportAdapterDispatcherTests
{
    private static (TransportAdapterDispatcher Dispatcher, FakeTransportAttachmentStore Store) NewDispatcher(
        params ITransportAdapter[] extraAdapters)
    {
        var adapters = new List<ITransportAdapter> { new NullTransportAdapter() };
        adapters.AddRange(extraAdapters);
        var registry = new TransportAdapterRegistry(adapters);
        var store = new FakeTransportAttachmentStore();
        return (new TransportAdapterDispatcher(registry, store, TimeProvider.System), store);
    }

    private static TransportAdapterTarget NullTarget(TransportAdapterRole role) => new(
        role,
        NullTransportAdapter.Id,
        HostAttachmentId: "id",
        HostAttachmentIdKind: NullTransportAdapter.HostAttachmentIdKindNull,
        AdapterContext: new Dictionary<string, string>());

    [Fact]
    public async Task Null_adapter_ReportStatus_returns_unobservable()
    {
        var (dispatcher, _) = NewDispatcher();
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Status.ShouldBe(RecordedStatus.Unobservable);
        res.Value.RecordedAt.ShouldBeNull();
        res.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
    }

    [Fact]
    public async Task Null_adapter_ProbeLiveness_returns_unknown_presence()
    {
        var (dispatcher, _) = NewDispatcher();
        var res = await dispatcher.ProbeLivenessAsync(NullTarget(TransportAdapterRole.Terminal), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Presence.ShouldBe(TransportLivenessPresence.Unknown);
        res.Value.RecordedAt.ShouldBeNull();
        res.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
    }

    [Fact]
    public async Task Null_adapter_Detach_writes_tombstone_and_returns_ok()
    {
        var (dispatcher, store) = NewDispatcher();
        var res = await dispatcher.DetachAsync(NullTarget(TransportAdapterRole.Terminal), expectedRevision: 3, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        // §6.1: undeclared capability still writes the tombstone at the record level.
        store.DetachCalls.Count.ShouldBe(1);
        store.DetachCalls[0].ExpectedRevision.ShouldBe(3);
    }

    [Fact]
    public async Task Null_adapter_Close_returns_close_not_supported_without_tombstone()
    {
        var (dispatcher, store) = NewDispatcher();
        var res = await dispatcher.CloseAsync(NullTarget(TransportAdapterRole.Terminal), expectedRevision: 5, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CloseNotSupported);
        // §6.2: no adapter close means no tombstone.
        store.CloseCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Null_adapter_PartialClose_returns_partial_close_not_supported()
    {
        var (dispatcher, _) = NewDispatcher();
        var scope = new PartialCloseScope("pane", "p-1", PartialCloseReason.UserRequested);
        var res = await dispatcher.PartialCloseAsync(NullTarget(TransportAdapterRole.Terminal), scope, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.PartialCloseNotSupported);
    }

    [Fact]
    public async Task Unregistered_adapter_id_never_falls_through_to_null()
    {
        var (dispatcher, _) = NewDispatcher();
        var target = new TransportAdapterTarget(
            TransportAdapterRole.Agent,
            AdapterId: "there-is-no-such-adapter",
            HostAttachmentId: "x",
            HostAttachmentIdKind: "kind",
            AdapterContext: new Dictionary<string, string>());
        var res = await dispatcher.CloseAsync(target, expectedRevision: 0, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.AdapterNotRegistered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(30_001)]
    [InlineData(int.MaxValue)]
    public async Task Probe_budget_outside_clamp_returns_probe_budget_invalid(int timeoutMs)
    {
        var (dispatcher, _) = NewDispatcher();
        var options = new TransportProbeOptions(TimeoutMs: timeoutMs);
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeBudgetInvalid);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(30_000)]
    public async Task Probe_budget_inside_clamp_is_accepted(int timeoutMs)
    {
        var (dispatcher, _) = NewDispatcher();
        var options = new TransportProbeOptions(TimeoutMs: timeoutMs);
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
    }

    // ─── Defect 3 — CheckCapabilityName rejects common-denominator names ───

    [Theory]
    [InlineData(TransportCapabilityExtensions.StatusReporting)]
    [InlineData(TransportCapabilityExtensions.LivenessProbe)]
    [InlineData(TransportCapabilityExtensions.Detach)]
    [InlineData(TransportCapabilityExtensions.Close)]
    [InlineData(TransportCapabilityExtensions.PartialClose)]
    public void CheckCapabilityName_accepts_optional_catalogue_names(string name)
    {
        var res = TransportAdapterDispatcher.CheckCapabilityName(name);
        res.IsSuccess.ShouldBeTrue(res.Error);
    }

    [Theory]
    [InlineData(TransportCapabilityExtensions.RecordIdentity)]
    [InlineData(TransportCapabilityExtensions.DescribeAdapter)]
    public void CheckCapabilityName_rejects_common_denominator_names(string name)
    {
        // §3.3's catalogue is exhaustive and EXCLUDES the common-
        // denominator names — they are not invocable as capabilities.
        var res = TransportAdapterDispatcher.CheckCapabilityName(name);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CapabilityNotDeclared);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SomethingElse")]
    [InlineData("lifecyclefacets")]
    public void CheckCapabilityName_rejects_unknown_names(string name)
    {
        var res = TransportAdapterDispatcher.CheckCapabilityName(name);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CapabilityNotDeclared);
    }

    // ─── Defect 7 — Adapter exceptions fold into probe-adapter-failed ───

    [Fact]
    public async Task ReportStatus_wraps_adapter_synchronous_exception_as_probe_adapter_failed()
    {
        var (dispatcher, _) = NewDispatcher(new ThrowingProbeAdapter(SyncThrow: true));
        var res = await dispatcher.ReportStatusAsync(ThrowingProbeAdapter.TargetOf(TransportAdapterRole.Agent), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ReportStatus_wraps_adapter_async_exception_as_probe_adapter_failed()
    {
        var (dispatcher, _) = NewDispatcher(new ThrowingProbeAdapter(SyncThrow: false));
        var res = await dispatcher.ReportStatusAsync(ThrowingProbeAdapter.TargetOf(TransportAdapterRole.Agent), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ProbeLiveness_wraps_adapter_synchronous_exception_as_probe_adapter_failed()
    {
        var (dispatcher, _) = NewDispatcher(new ThrowingProbeAdapter(SyncThrow: true));
        var res = await dispatcher.ProbeLivenessAsync(ThrowingProbeAdapter.TargetOf(TransportAdapterRole.Agent), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ProbeLiveness_wraps_adapter_async_exception_as_probe_adapter_failed()
    {
        var (dispatcher, _) = NewDispatcher(new ThrowingProbeAdapter(SyncThrow: false));
        var res = await dispatcher.ProbeLivenessAsync(ThrowingProbeAdapter.TargetOf(TransportAdapterRole.Agent), options: null, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeAdapterFailed);
    }

    [Fact]
    public async Task ReportStatus_preserves_caller_cancellation()
    {
        var (dispatcher, _) = NewDispatcher(new CancellingProbeAdapter());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<System.OperationCanceledException>(async () =>
            await dispatcher.ReportStatusAsync(CancellingProbeAdapter.TargetOf(TransportAdapterRole.Agent), options: null, cts.Token));
    }

    // ─── Defect 8 — Coordinating detach / close ───

    [Fact]
    public async Task Detach_with_undeclared_capability_writes_tombstone_and_returns_ok()
    {
        // Path 1: capability not declared. Store MUST be called even
        // though the adapter isn't (per §6.1: detach at the record
        // level is always available).
        var (dispatcher, store) = NewDispatcher();
        var res = await dispatcher.DetachAsync(NullTarget(TransportAdapterRole.Terminal), expectedRevision: 7, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        store.DetachCalls.Count.ShouldBe(1);
        store.DetachCalls[0].ExpectedRevision.ShouldBe(7);
    }

    [Fact]
    public async Task Detach_with_declared_adapter_failure_still_writes_tombstone_and_returns_detach_adapter_failed()
    {
        // Path 2: adapter declared, adapter returned Fail. Tombstone
        // MUST still be written (§6.1 CAS-advance-on-failure rule) and
        // the caller sees transport-detach-adapter-failed.
        var (dispatcher, store) = NewDispatcher(new FailingDetachAdapter());
        var res = await dispatcher.DetachAsync(FailingDetachAdapter.TargetOf(TransportAdapterRole.Agent), expectedRevision: 9, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.DetachAdapterFailed);
        store.DetachCalls.Count.ShouldBe(1);
        store.DetachCalls[0].ExpectedRevision.ShouldBe(9);
    }

    [Fact]
    public async Task Close_with_declared_adapter_success_writes_tombstone_and_returns_ok()
    {
        // Path 3: adapter succeeds; store tombstone written; Ok.
        var (dispatcher, store) = NewDispatcher(new SucceedingCloseAdapter());
        var res = await dispatcher.CloseAsync(SucceedingCloseAdapter.TargetOf(TransportAdapterRole.Agent), expectedRevision: 11, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        store.CloseCalls.Count.ShouldBe(1);
        store.CloseCalls[0].ExpectedRevision.ShouldBe(11);
    }

    [Fact]
    public async Task Close_with_declared_adapter_failure_does_not_write_tombstone()
    {
        // Path 4: adapter failed; NO tombstone; caller sees close-adapter-failed.
        var (dispatcher, store) = NewDispatcher(new FailingCloseAdapter());
        var res = await dispatcher.CloseAsync(FailingCloseAdapter.TargetOf(TransportAdapterRole.Agent), expectedRevision: 13, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CloseAdapterFailed);
        store.CloseCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Close_with_adapter_throwing_surfaces_close_adapter_failed_and_writes_no_tombstone()
    {
        var (dispatcher, store) = NewDispatcher(new ThrowingCloseAdapter());
        var res = await dispatcher.CloseAsync(ThrowingCloseAdapter.TargetOf(TransportAdapterRole.Agent), expectedRevision: 15, CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CloseAdapterFailed);
        store.CloseCalls.ShouldBeEmpty();
    }

    // ─── test adapter fakes ───

    private sealed class ThrowingProbeAdapter : ITransportAdapter
    {
        private const string Id = "throwing-probe";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability>
        {
            TransportCapability.StatusReporting, TransportCapability.LivenessProbe,
        };
        private readonly bool _syncThrow;
        public ThrowingProbeAdapter(bool SyncThrow) { _syncThrow = SyncThrow; }

        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) =>
            throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() =>
            new(Id, "throwing", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "throwing");

        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        {
            if (_syncThrow) throw new System.InvalidOperationException("sync boom");
            return AsyncThrow<TransportStatusObservation>();
        }
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        {
            if (_syncThrow) throw new System.InvalidOperationException("sync boom");
            return AsyncThrow<TransportLivenessObservation>();
        }
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();

        private static async Task<Result<T>> AsyncThrow<T>()
        {
            await Task.Yield();
            throw new System.InvalidOperationException("async boom");
        }

        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }

    private sealed class CancellingProbeAdapter : ITransportAdapter
    {
        private const string Id = "cancelling-probe";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability>
        {
            TransportCapability.StatusReporting,
        };
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(Id, "cancelling", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "cancelling");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new System.OperationCanceledException(ct);
        }
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();

        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }

    private sealed class FailingDetachAdapter : ITransportAdapter
    {
        private const string Id = "failing-detach";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability> { TransportCapability.Detach };
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(Id, "failing-detach", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "failing-detach");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) =>
            Task.FromResult(Result.Fail(TransportAttachmentFailure.DetachAdapterFailed));
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();
        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }

    private sealed class SucceedingCloseAdapter : ITransportAdapter
    {
        private const string Id = "succeeding-close";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability> { TransportCapability.Close };
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(Id, "succeeding-close", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "succeeding-close");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => Task.FromResult(Result.Ok());
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();
        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }

    private sealed class FailingCloseAdapter : ITransportAdapter
    {
        private const string Id = "failing-close";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability> { TransportCapability.Close };
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(Id, "failing-close", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "failing-close");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) =>
            Task.FromResult(Result.Fail(TransportAttachmentFailure.CloseAdapterFailed));
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();
        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }

    private sealed class ThrowingCloseAdapter : ITransportAdapter
    {
        private const string Id = "throwing-close";
        public string AdapterId => Id;
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability> { TransportCapability.Close };
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(Id, "throwing-close", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "throwing-close");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.InvalidOperationException("close boom");
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();
        public static TransportAdapterTarget TargetOf(TransportAdapterRole role) => new(role, Id, "x", "kind", new Dictionary<string, string>());
    }
}
