using System.Threading;
using System.Threading.Tasks;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §10.1 <c>ChangeProposalRenderer</c>. The single workflow-
/// domain integration point that reads a
/// <see cref="TransportAttachmentRecord"/> — and does so ONLY to pick
/// a rendering (§10.1). Every other workflow surface — validator,
/// store, adapter dispatch, probes, detach, close, conformance tests —
/// reads the record too; the earlier "reads the record at all" wording
/// overstated the constraint (§10.1).
/// </summary>
internal interface IChangeProposalRenderer
{
    /// <summary>Contract §10.2 four-clause selection rule; the first
    /// satisfied clause wins.
    /// <list type="number">
    ///   <item><paramref name="transportRecord"/> is <c>null</c>, or
    ///     the adapter for the top-priority payload is unregistered →
    ///     <see cref="Presentation.TerminalText.Instance"/>.</item>
    ///   <item>Agent payload is present AND the support registry knows
    ///     the agent adapter → rich-adapter presentation targeting the
    ///     agent.</item>
    ///   <item>Terminal payload is present AND the support registry
    ///     knows the terminal adapter → rich-adapter presentation
    ///     targeting the terminal.</item>
    ///   <item>Otherwise →
    ///     <see cref="Presentation.TerminalText.Instance"/>.</item>
    /// </list>
    /// A caller passing a <see cref="Twig.Domain.Common.Result{T}"/>-shaped
    /// read that failed MUST pass <c>null</c> here so clause 1 applies
    /// (§10.2 "unreadable" case).</summary>
    Presentation SelectPresentation(
        ChangeProposalRenderProposal proposal,
        TransportAttachmentRecord? transportRecord);

    /// <summary>Contract §10.3 invocation-time rendering with
    /// unconditional fallback.
    /// <list type="number">
    ///   <item>Terminal-text presentation → terminal-text render.</item>
    ///   <item>Rich-adapter presentation:
    ///     <list type="bullet">
    ///       <item>Rich renderer unavailable at invocation time (not
    ///         registered, throws on construction, or its precondition
    ///         fails) → terminal-text fallback with UNCHANGED
    ///         proposal.</item>
    ///       <item>Rich renderer returns "refused", throws, or times
    ///         out under its own contract-defined budget → terminal-
    ///         text fallback with UNCHANGED proposal.</item>
    ///       <item>Otherwise → rich renderer's
    ///         <see cref="RenderedProposal"/>.</item>
    ///     </list>
    ///   </item>
    /// </list>
    /// The terminal/text renderer is guaranteed available in every
    /// build because AB#745 registers it (§10.3). No case may alter
    /// <paramref name="proposal"/>'s content; the fallback renders the
    /// same proposal payload the rich path attempted.</summary>
    Task<RenderedProposal> RenderAsync(
        ChangeProposalRenderProposal proposal,
        Presentation presentation,
        CancellationToken ct = default);
}

/// <summary>Contract §10.3 rich-renderer abstraction. Renderers
/// registered here are the ones the support registry answers
/// <see cref="IChangeProposalPresentationSupportRegistry.IsSupported"/>
/// with <c>true</c> for. A renderer that raises
/// <see cref="System.Exception"/>, returns <c>null</c>, or returns a
/// <see cref="RichRenderResult.Refused"/> outcome triggers the
/// §10.3(b) terminal-text fallback.</summary>
internal interface IRichChangeProposalRenderer
{
    RichAdapterId Id { get; }

    /// <summary>Contract-defined render budget in milliseconds. The
    /// dispatcher wraps invocation in this budget (§10.3(b) timeout).
    /// A non-positive value means "no wrapper timeout — the renderer's
    /// own contract governs".</summary>
    int TimeoutMs { get; }

    Task<RichRenderResult> RenderAsync(ChangeProposalRenderProposal proposal, CancellationToken ct);
}

/// <summary>Contract §10.3 renderer outcome. Named result rather than
/// a nullable return so a "refused" outcome cannot be confused with a
/// null-safety bug in the renderer.</summary>
internal abstract record RichRenderResult
{
    private RichRenderResult() { }

    public sealed record Rendered(RenderedProposal Proposal) : RichRenderResult;
    public sealed record Refused(string Reason) : RichRenderResult;
}

/// <summary>Contract §10.4 terminal/text renderer. Guaranteed
/// available in every build (§10.3). Renders every proposal in full,
/// on every host, and is the presentation every authorization decision
/// branches against.</summary>
internal interface ITerminalTextChangeProposalRenderer
{
    RenderedProposal Render(ChangeProposalRenderProposal proposal);
}

/// <summary>Baseline terminal/text renderer — echoes the proposal id
/// and passthrough content into a <see cref="RenderedProposal"/>. The
/// visible output shape is owned by the terminal renderer (§10.2);
/// this baseline is enough for the seam.</summary>
internal sealed class TerminalTextChangeProposalRenderer : ITerminalTextChangeProposalRenderer
{
    public RenderedProposal Render(ChangeProposalRenderProposal proposal) =>
        new(
            PresentationKind: RenderedProposalKind.TerminalText,
            AdapterId: null,
            Body: proposal.Content);
}
