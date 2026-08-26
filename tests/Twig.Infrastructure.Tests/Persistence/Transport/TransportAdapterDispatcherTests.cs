using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// §3.2 dispatch and null-adapter conformance. Verifies the
/// absent-capability degradation table row-by-row for the null
/// adapter, and verifies §7.3's "unregistered adapter does NOT
/// silently fall through to null" rule.
/// </summary>
public sealed class TransportAdapterDispatcherTests
{
    private static TransportAdapterDispatcher NewDispatcher()
    {
        var registry = new TransportAdapterRegistry(new ITransportAdapter[] { new NullTransportAdapter() });
        return new TransportAdapterDispatcher(registry, TimeProvider.System);
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
        var dispatcher = NewDispatcher();
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options: null, System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Status.ShouldBe(RecordedStatus.Unobservable);
        res.Value.RecordedAt.ShouldBeNull();
        res.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
    }

    [Fact]
    public async Task Null_adapter_ProbeLiveness_returns_unknown_presence()
    {
        var dispatcher = NewDispatcher();
        var res = await dispatcher.ProbeLivenessAsync(NullTarget(TransportAdapterRole.Terminal), options: null, System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
        res.Value.Presence.ShouldBe(TransportLivenessPresence.Unknown);
        res.Value.RecordedAt.ShouldBeNull();
        res.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
    }

    [Fact]
    public async Task Null_adapter_Detach_returns_ok()
    {
        var dispatcher = NewDispatcher();
        var res = await dispatcher.DetachAsync(NullTarget(TransportAdapterRole.Terminal), System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Null_adapter_Close_returns_close_not_supported()
    {
        var dispatcher = NewDispatcher();
        var res = await dispatcher.CloseAsync(NullTarget(TransportAdapterRole.Terminal), System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.CloseNotSupported);
    }

    [Fact]
    public async Task Null_adapter_PartialClose_returns_partial_close_not_supported()
    {
        var dispatcher = NewDispatcher();
        var scope = new PartialCloseScope("pane", "p-1", PartialCloseReason.UserRequested);
        var res = await dispatcher.PartialCloseAsync(NullTarget(TransportAdapterRole.Terminal), scope, System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.PartialCloseNotSupported);
    }

    [Fact]
    public async Task Unregistered_adapter_id_never_falls_through_to_null()
    {
        var dispatcher = NewDispatcher();
        var target = new TransportAdapterTarget(
            TransportAdapterRole.Agent,
            AdapterId: "there-is-no-such-adapter",
            HostAttachmentId: "x",
            HostAttachmentIdKind: "kind",
            AdapterContext: new Dictionary<string, string>());
        var res = await dispatcher.CloseAsync(target, System.Threading.CancellationToken.None);
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
        var dispatcher = NewDispatcher();
        var options = new TransportProbeOptions(TimeoutMs: timeoutMs);
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options, System.Threading.CancellationToken.None);
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(TransportAttachmentFailure.ProbeBudgetInvalid);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(30_000)]
    public async Task Probe_budget_inside_clamp_is_accepted(int timeoutMs)
    {
        var dispatcher = NewDispatcher();
        var options = new TransportProbeOptions(TimeoutMs: timeoutMs);
        var res = await dispatcher.ReportStatusAsync(NullTarget(TransportAdapterRole.Terminal), options, System.Threading.CancellationToken.None);
        // Null adapter degrades — the clamp check passes and the
        // degradation observation is returned.
        res.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(TransportCapabilityExtensions.StatusReporting)]
    [InlineData(TransportCapabilityExtensions.LivenessProbe)]
    [InlineData(TransportCapabilityExtensions.Detach)]
    [InlineData(TransportCapabilityExtensions.Close)]
    [InlineData(TransportCapabilityExtensions.PartialClose)]
    [InlineData(TransportCapabilityExtensions.RecordIdentity)]
    [InlineData(TransportCapabilityExtensions.DescribeAdapter)]
    public void CheckCapabilityName_accepts_catalogue_names(string name)
    {
        var res = TransportAdapterDispatcher.CheckCapabilityName(name);
        res.IsSuccess.ShouldBeTrue(res.Error);
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
}
