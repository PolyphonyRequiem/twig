using Twig.Domain.Services.Process;

namespace Twig.Mcp.Tools;

/// <summary>The agent surface's rendered document.</summary>
internal sealed record RenderedProcessDescription(string Document);

/// <summary>
/// The outcome of rendering the agent surface's process description.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 The three failure arms are the DOMAIN's own case types, reused rather than mirrored
/// (AB#244). A parallel set here would be free to drift from the assembler's, which is the
/// class of duplication this feature already refuses for the document itself. Only the success
/// arm differs: at this surface the answer is rendered bytes, not a model.
/// </para>
/// <para>
/// 🔴 A consequence worth knowing before adding a third consumer: because the arms are shared,
/// this union and <see cref="ProcessDescriptionResult"/> are NOT disjoint, so a bare
/// <c>result is ProcessIdentityUnresolved</c> does not by itself tell a reader which union is
/// in hand. Every <c>UnreachableException</c> over either union therefore names its OWN union
/// in the message — keep that discipline, it is the disambiguation a reader needs at a failure.
/// </para>
/// </remarks>
internal union ProcessDescriptionRenderResult(
    RenderedProcessDescription,
    ProcessIdentityUnresolved,
    ProcessTypesUnfetchable,
    ProcessDescriptionTypeNotFound);
