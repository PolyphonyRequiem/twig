using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Twig.Domain.Common;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// §10.1–§10.4 selection + render tests. Covers the four-clause
/// selection rule (including §10.2 clause 1 adapter-registry gate),
/// the invocation-time refusal fallback
/// (<c>RichRenderersAllDecline</c>), the §10.3 rich-renderer
/// construction-failure fallback, and the universal terminal/text
/// fallback (§10.4).
/// </summary>
public sealed class ChangeProposalRendererTests
{
    private static readonly IReadOnlyDictionary<string, string> _ctx = new Dictionary<string, string>();
    private static readonly IReadOnlySet<TransportCapability> _noCaps = new HashSet<TransportCapability>();

    private static ChangeProposalRenderProposal Proposal(string id = "cp-1", object? content = null) =>
        new(id, content ?? "canonical-content", _ctx);

    private static TransportAttachmentRecord DirectHumanRecord(string terminalAdapter = "null") =>
        new(
            Worktree: new TransportWorktreePayload("{fp}",
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "null", "w", "null", _ctx)),
            Agent: null,
            Terminal: new TransportTerminalPayload(
                new TransportAdapterTarget(TransportAdapterRole.Terminal, terminalAdapter, "t", "null", _ctx),
                _noCaps));

    private static TransportAttachmentRecord AgentDrivenRecord(string agentAdapter = "herdr", string? terminalAdapter = "wt") =>
        new(
            Worktree: new TransportWorktreePayload("{fp}",
                new TransportAdapterTarget(TransportAdapterRole.Worktree, "herdr", "w", "kind", _ctx)),
            Agent: new TransportAgentPayload(
                new TransportAdapterTarget(TransportAdapterRole.Agent, agentAdapter, "a", "kind", _ctx),
                "cli", RecordedStatus.Working, System.DateTimeOffset.UnixEpoch, _noCaps),
            Terminal: terminalAdapter is null ? null : new TransportTerminalPayload(
                new TransportAdapterTarget(TransportAdapterRole.Terminal, terminalAdapter, "t", "kind", _ctx),
                _noCaps));

    private static ChangeProposalRenderer MakeRenderer(
        IEnumerable<RichAdapterId>? supported = null,
        IEnumerable<IRichChangeProposalRenderer>? richRenderers = null,
        IEnumerable<string>? registeredAdapterIds = null,
        System.Func<IEnumerable<IRichChangeProposalRenderer>>? richFactoryOverride = null)
    {
        var supportRegistry = new ChangeProposalPresentationSupportRegistry(supported ?? System.Array.Empty<RichAdapterId>());
        var text = new TerminalTextChangeProposalRenderer();
        HashSet<string> adapterIds;
        if (registeredAdapterIds is not null)
        {
            adapterIds = new HashSet<string>(registeredAdapterIds);
        }
        else
        {
            // Every test with a rich adapter id in `supported` implicitly
            // wants that adapter registered too (baseline behaviour).
            adapterIds = new HashSet<string>(
                System.Linq.Enumerable.Select(supported ?? System.Array.Empty<RichAdapterId>(), s => s.AdapterId));
            // Plus the always-present "null" and default herdr/wt so
            // agent-driven records with default adapters pass the
            // clause-1 gate. Tests wanting the gate to trip explicitly
            // pass `registeredAdapterIds`.
            adapterIds.Add("null"); adapterIds.Add("herdr"); adapterIds.Add("wt");
        }
        var adapterList = new List<ITransportAdapter>();
        foreach (var id in adapterIds)
            adapterList.Add(new StubAdapter(id));
        var adapterRegistry = new TransportAdapterRegistry(adapterList);
        System.Func<IEnumerable<IRichChangeProposalRenderer>> factory =
            richFactoryOverride ?? (() => richRenderers ?? System.Array.Empty<IRichChangeProposalRenderer>());
        return new ChangeProposalRenderer(supportRegistry, adapterRegistry, text, factory);
    }

    // ─── §10.2 four-clause selection ───

    [Fact]
    public void Null_transport_record_selects_terminal_text()
    {
        var r = MakeRenderer();
        var p = r.SelectPresentation(Proposal(), transportRecord: null);
        p.ShouldBeOfType<Presentation.TerminalText>();
    }

    [Fact]
    public void Agent_preferred_over_terminal_when_both_supported()
    {
        var r = MakeRenderer(new[]
        {
            new RichAdapterId("herdr", TransportAdapterRole.Agent),
            new RichAdapterId("wt", TransportAdapterRole.Terminal),
        });
        var p = r.SelectPresentation(Proposal(), AgentDrivenRecord());
        var rich = p.ShouldBeOfType<Presentation.RichAdapter>();
        rich.AdapterId.AdapterId.ShouldBe("herdr");
        rich.AdapterId.Role.ShouldBe(TransportAdapterRole.Agent);
    }

    [Fact]
    public void Terminal_selected_when_agent_unsupported_but_terminal_supported()
    {
        var r = MakeRenderer(new[] { new RichAdapterId("wt", TransportAdapterRole.Terminal) },
            registeredAdapterIds: new[] { "wt", "unknown-agent" });
        var p = r.SelectPresentation(Proposal(), AgentDrivenRecord(agentAdapter: "unknown-agent", terminalAdapter: "wt"));
        var rich = p.ShouldBeOfType<Presentation.RichAdapter>();
        rich.AdapterId.Role.ShouldBe(TransportAdapterRole.Terminal);
    }

    [Fact]
    public void No_supported_adapters_falls_back_to_terminal_text()
    {
        var r = MakeRenderer();
        var p = r.SelectPresentation(Proposal(), AgentDrivenRecord());
        p.ShouldBeOfType<Presentation.TerminalText>();
    }

    [Fact]
    public void Direct_human_terminal_supported_selects_rich()
    {
        var r = MakeRenderer(new[] { new RichAdapterId("wt", TransportAdapterRole.Terminal) });
        var p = r.SelectPresentation(Proposal(), DirectHumanRecord(terminalAdapter: "wt"));
        var rich = p.ShouldBeOfType<Presentation.RichAdapter>();
        rich.AdapterId.Role.ShouldBe(TransportAdapterRole.Terminal);
    }

    // ─── Defect 5 — §10.2 clause 1 adapter-registry gate ───

    [Fact]
    public void Clause1_unregistered_agent_adapter_forces_terminal_text_even_when_terminal_registered_and_supported()
    {
        // Mixed case named by the reviewer: agent adapterId unregistered
        // AND terminal registered+supported. Clause 1 MUST force
        // TerminalText BEFORE clauses 2/3.
        var r = MakeRenderer(
            supported: new[] { new RichAdapterId("wt", TransportAdapterRole.Terminal) },
            registeredAdapterIds: new[] { "wt" }); // 'unknown-agent' deliberately absent
        var p = r.SelectPresentation(
            Proposal(),
            AgentDrivenRecord(agentAdapter: "unknown-agent", terminalAdapter: "wt"));
        p.ShouldBeOfType<Presentation.TerminalText>();
    }

    [Fact]
    public void Clause1_unregistered_terminal_adapter_forces_terminal_text_even_when_agent_registered_and_supported()
    {
        // Symmetric: agent registered+supported, terminal unregistered.
        // Clause 1 still gates because ANY referenced-adapter is
        // unregistered — a record we can't trust is TerminalText.
        var r = MakeRenderer(
            supported: new[] { new RichAdapterId("herdr", TransportAdapterRole.Agent) },
            registeredAdapterIds: new[] { "herdr" }); // 'wt' deliberately absent
        var p = r.SelectPresentation(
            Proposal(),
            AgentDrivenRecord(agentAdapter: "herdr", terminalAdapter: "wt"));
        p.ShouldBeOfType<Presentation.TerminalText>();
    }

    [Fact]
    public void Clause1_direct_human_unregistered_terminal_adapter_forces_terminal_text()
    {
        var r = MakeRenderer(
            supported: new[] { new RichAdapterId("wt", TransportAdapterRole.Terminal) },
            registeredAdapterIds: new string[] { }); // 'wt' absent
        var p = r.SelectPresentation(Proposal(), DirectHumanRecord(terminalAdapter: "wt"));
        p.ShouldBeOfType<Presentation.TerminalText>();
    }

    // ─── Defect 6 — Rich-renderer construction failure fallback ───

    [Fact]
    public async Task Rich_renderer_construction_throw_falls_back_to_terminal_text()
    {
        // §10.3(a): "throws on construction" MUST fall back to
        // terminal-text with unchanged content. The factory is invoked
        // INSIDE the render-time try/fallback boundary, so a constructor
        // throw here does not defeat the whole renderer service.
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var r = MakeRenderer(
            supported: new[] { richId },
            richFactoryOverride: () => throw new System.InvalidOperationException("constructor boom"));
        var proposal = Proposal("cp-defect-6", content: "content-DEFECT6");
        var result = await r.RenderAsync(proposal, new Presentation.RichAdapter(richId));
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        result.AdapterId.ShouldBeNull();
        result.Body.ShouldBe("content-DEFECT6");
    }

    [Fact]
    public void Rich_renderer_construction_throw_does_not_prevent_ChangeProposalRenderer_from_resolving()
    {
        // §10.3 guarantee at the wiring level: constructing the
        // ChangeProposalRenderer itself never enumerates rich renderers.
        // A factory that throws on invocation is fine at construction
        // time (only touched inside RenderAsync's try boundary).
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        Should.NotThrow(() => MakeRenderer(
            supported: new[] { richId },
            richFactoryOverride: () => throw new System.InvalidOperationException("boom")));
    }

    // ─── §10.3 refusal / throw / not-registered fallbacks (existing) ───

    [Fact]
    public async Task Rich_renderer_refusal_falls_back_to_terminal_text()
    {
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var r = MakeRenderer(
            new[] { richId },
            new IRichChangeProposalRenderer[] { new RefusingRichRenderer(richId) });
        var proposal = Proposal("cp-42", content: "content-A");
        var result = await r.RenderAsync(proposal, new Presentation.RichAdapter(richId));
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        result.AdapterId.ShouldBeNull();
        result.Body.ShouldBe("content-A");
    }

    [Fact]
    public async Task Rich_renderer_throwing_falls_back_to_terminal_text()
    {
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var r = MakeRenderer(
            new[] { richId },
            new IRichChangeProposalRenderer[] { new ThrowingRichRenderer(richId) });
        var proposal = Proposal();
        var result = await r.RenderAsync(proposal, new Presentation.RichAdapter(richId));
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
    }

    [Fact]
    public async Task Rich_renderer_not_registered_falls_back_to_terminal_text()
    {
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var r = MakeRenderer(new[] { richId });
        var result = await r.RenderAsync(Proposal(), new Presentation.RichAdapter(richId));
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
    }

    [Fact]
    public async Task Rich_renderer_success_returns_rich_rendered_proposal()
    {
        var richId = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var rendered = new RenderedProposal(RenderedProposalKind.RichAdapter, richId.AdapterId, "rich-body");
        var r = MakeRenderer(
            new[] { richId },
            new IRichChangeProposalRenderer[] { new SucceedingRichRenderer(richId, rendered) });
        var result = await r.RenderAsync(Proposal(), new Presentation.RichAdapter(richId));
        result.PresentationKind.ShouldBe(RenderedProposalKind.RichAdapter);
        result.AdapterId.ShouldBe(richId.AdapterId);
        result.Body.ShouldBe("rich-body");
    }

    [Fact]
    public async Task TerminalText_presentation_always_renders_the_proposal()
    {
        var r = MakeRenderer();
        var proposal = Proposal(content: "authoritative-content");
        var result = await r.RenderAsync(proposal, Presentation.TerminalText.Instance);
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        result.Body.ShouldBe("authoritative-content");
    }

    [Fact]
    public async Task RichRenderersAllDecline_conformance_falls_back_with_unchanged_content()
    {
        var richA = new RichAdapterId("wt", TransportAdapterRole.Terminal);
        var richB = new RichAdapterId("herdr", TransportAdapterRole.Agent);
        var r = MakeRenderer(
            new[] { richA, richB },
            new IRichChangeProposalRenderer[]
            {
                new RefusingRichRenderer(richA),
                new RefusingRichRenderer(richB),
            });
        var proposal = Proposal(content: "authorization-relevant-body");
        var selection = r.SelectPresentation(proposal, AgentDrivenRecord(agentAdapter: "herdr", terminalAdapter: "wt"));
        var result = await r.RenderAsync(proposal, selection);
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        result.Body.ShouldBe("authorization-relevant-body");
    }

    // ─── fakes ───

    private sealed class StubAdapter : ITransportAdapter
    {
        public StubAdapter(string id) { AdapterId = id; }
        public string AdapterId { get; }
        public IReadOnlySet<TransportCapability> Capabilities { get; } = new HashSet<TransportCapability>();
        public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request) => throw new System.NotSupportedException();
        public AdapterDescription DescribeAdapter() => new(AdapterId, "stub", "1", Capabilities, new System.Collections.Generic.HashSet<TransportAdapterRole>(), "stub");
        public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct) => throw new System.NotSupportedException();
        public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct) => throw new System.NotSupportedException();
    }

    private sealed class RefusingRichRenderer : IRichChangeProposalRenderer
    {
        public RefusingRichRenderer(RichAdapterId id) { Id = id; }
        public RichAdapterId Id { get; }
        public int TimeoutMs => 0;
        public Task<RichRenderResult> RenderAsync(ChangeProposalRenderProposal proposal, CancellationToken ct) =>
            Task.FromResult<RichRenderResult>(new RichRenderResult.Refused("test-refusal"));
    }

    private sealed class ThrowingRichRenderer : IRichChangeProposalRenderer
    {
        public ThrowingRichRenderer(RichAdapterId id) { Id = id; }
        public RichAdapterId Id { get; }
        public int TimeoutMs => 0;
        public Task<RichRenderResult> RenderAsync(ChangeProposalRenderProposal proposal, CancellationToken ct) =>
            throw new System.InvalidOperationException("boom");
    }

    private sealed class SucceedingRichRenderer : IRichChangeProposalRenderer
    {
        private readonly RenderedProposal _rendered;
        public SucceedingRichRenderer(RichAdapterId id, RenderedProposal rendered) { Id = id; _rendered = rendered; }
        public RichAdapterId Id { get; }
        public int TimeoutMs => 0;
        public Task<RichRenderResult> RenderAsync(ChangeProposalRenderProposal proposal, CancellationToken ct) =>
            Task.FromResult<RichRenderResult>(new RichRenderResult.Rendered(_rendered));
    }
}
