using System.Collections.Generic;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §10.2 <c>ChangeProposalRenderProposal</c>. The workflow-domain
/// consumer's opaque payload passed unchanged from
/// <see cref="IChangeProposalRenderer.SelectPresentation"/> through
/// <see cref="IChangeProposalRenderer.Render"/>.
/// <para>
/// <see cref="Content"/> is owned by the Change Proposal design and is
/// not inspected here beyond passthrough (§10.5 defers the concrete
/// content shape). AB#745 does NOT implement it — a placeholder
/// <see cref="object"/> passthrough is sufficient for the seam.
/// </para>
/// </summary>
internal sealed record ChangeProposalRenderProposal(
    string ProposalId,
    object Content,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Contract §10.2 <c>RichAdapterId</c>. Deterministic key for
/// the presentation-support registry lookup — string equality against
/// <see cref="AdapterId"/> plus <see cref="Role"/>.</summary>
internal readonly record struct RichAdapterId(string AdapterId, TransportAdapterRole Role);

/// <summary>Contract §10.2 <c>Presentation</c>. The abstract root of a
/// two-variant discriminated union — <see cref="TerminalText"/> or
/// <see cref="RichAdapter"/>. Kept as a sealed abstract record so the
/// four-clause selection rule (§10.2) is exhaustive by construction.
/// </summary>
internal abstract record Presentation
{
    private Presentation() { }

    /// <summary>Contract §10.2 <c>TerminalTextPresentation</c>. The
    /// universal fallback that MUST remain unconditional (§10.4) — it
    /// renders every proposal in full, on every host, and is the
    /// presentation used by every authorization decision.</summary>
    public sealed record TerminalText : Presentation
    {
        /// <summary>Singleton — the presentation carries no state; every
        /// selector returns the same reference so equality is free.
        /// </summary>
        public static readonly TerminalText Instance = new();
    }

    /// <summary>Contract §10.2 <c>RichAdapterPresentation</c>. Selected
    /// only when the four-clause rule reaches clause 2 or 3.</summary>
    public sealed record RichAdapter(RichAdapterId AdapterId) : Presentation;
}

/// <summary>Contract §10.2 <c>RenderedProposal</c>. Output of
/// <see cref="IChangeProposalRenderer.Render"/>. Discriminated by
/// <see cref="PresentationKind"/> so callers can branch without
/// re-inspecting the presentation.
/// <para>
/// <see cref="AdapterId"/> is non-null only when
/// <see cref="PresentationKind"/> = <see cref="RenderedProposalKind.RichAdapter"/>.
/// The visible output shape is owned by the terminal/rich renderer and
/// is not fixed here beyond the presentation-kind discriminator (§10.2).
/// </para>
/// </summary>
internal sealed record RenderedProposal(
    RenderedProposalKind PresentationKind,
    string? AdapterId,
    object? Body);

/// <summary>Wire form of <see cref="RenderedProposal.PresentationKind"/>
/// per §10.2.</summary>
internal enum RenderedProposalKind
{
    TerminalText = 0,
    RichAdapter = 1,
}
