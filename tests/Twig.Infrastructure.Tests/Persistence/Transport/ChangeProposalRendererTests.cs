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
/// selection rule, the invocation-time refusal fallback
/// (<c>RichRenderersAllDecline</c>), and the universal terminal/text
/// fallback (§10.4). Corresponds to the AB#745 §12.1 acceptance line:
/// "a test where every adapter is refused still authorizes via the
/// terminal/text fallback with the authorization decision unchanged".
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
        IEnumerable<IRichChangeProposalRenderer>? richRenderers = null)
    {
        var registry = new ChangeProposalPresentationSupportRegistry(supported ?? System.Array.Empty<RichAdapterId>());
        var text = new TerminalTextChangeProposalRenderer();
        return new ChangeProposalRenderer(registry, text, richRenderers ?? System.Array.Empty<IRichChangeProposalRenderer>());
    }

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
        var r = MakeRenderer(new[] { new RichAdapterId("wt", TransportAdapterRole.Terminal) });
        var p = r.SelectPresentation(Proposal(), AgentDrivenRecord(agentAdapter: "unknown-agent", terminalAdapter: "wt"));
        var rich = p.ShouldBeOfType<Presentation.RichAdapter>();
        rich.AdapterId.Role.ShouldBe(TransportAdapterRole.Terminal);
    }

    [Fact]
    public void No_supported_adapters_falls_back_to_terminal_text()
    {
        var r = MakeRenderer(); // support registry empty
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
        // §10.3: content is UNCHANGED.
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
        // Supported by registry, but no renderer implementation registered.
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

    // §10.3 RichRenderersAllDecline conformance case:
    // every registered rich renderer refuses; render still produces
    // terminal-text with the proposal's content preserved.
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
        // Pick agent path — refusal must still fall through.
        var selection = r.SelectPresentation(proposal, AgentDrivenRecord(agentAdapter: "herdr", terminalAdapter: "wt"));
        var result = await r.RenderAsync(proposal, selection);
        result.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        result.Body.ShouldBe("authorization-relevant-body");
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
