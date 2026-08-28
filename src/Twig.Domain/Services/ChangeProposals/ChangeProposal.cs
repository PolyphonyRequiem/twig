using Twig.Domain.Services.Plan;

namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// An immutable, digest-bound mutation document — the Change Proposal of Spec #729.
/// <para>
/// Per design record T2 (AB#741) the Plan v1 document <em>is</em> the Change Proposal; that
/// contract is ratified unchanged rather than reinvented, so a proposal is a
/// <see cref="PlanDefinition"/> plus the canonical byte form and digest the shared
/// canonicalizer produced for it.
/// </para>
/// <para>
/// <b>Immutability.</b> Every member is <c>init</c>-only on a <c>sealed record</c>, and
/// <see cref="PlanDefinition"/> exposes its operations as <see cref="IReadOnlyList{T}"/>.
/// There is no member through which semantic content can be rewritten after construction,
/// so the "a proposal cannot be mutated after rendering" rule is enforced by the type
/// rather than by a runtime guard that could be forgotten at a new call site.
/// </para>
/// <para>
/// <b>Digest.</b> <see cref="Digest"/> is always the digest of <see cref="CanonicalJson"/>,
/// computed by the same canonicalizer the validate/preview/apply path uses. It is therefore
/// stable across rendering, preview and later journal lookup by construction, not by a
/// parallel implementation that could drift. Nothing learned after parse — a revision
/// returned by a PATCH, a published id, a readback warning — ever enters it.
/// </para>
/// </summary>
public sealed record ChangeProposal
{
    /// <summary>The validated Plan v1 document this proposal carries.</summary>
    public required PlanDefinition Definition { get; init; }

    /// <summary>
    /// Canonical UTF-8 JSON form: object properties sorted ordinal-ascending, array order
    /// preserved, compact. Two source files differing only in whitespace or property order
    /// reduce to the same value here.
    /// </summary>
    public required string CanonicalJson { get; init; }

    /// <summary>
    /// Lowercase-hex SHA-256 of <see cref="CanonicalJson"/>'s UTF-8 bytes — exactly 64
    /// characters, no prefix, no truncation.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    /// The Change Recipe this proposal was rendered from, or <c>null</c> when the proposal
    /// was authored ad hoc. A reviewer uses this to navigate back to the template.
    /// </summary>
    public ChangeRecipeReference? Recipe { get; init; }

    /// <summary>
    /// Why this proposal exists. Free text supplied by the author; <c>null</c> when absent.
    /// Never part of the digest.
    /// </summary>
    public string? Rationale { get; init; }
}
