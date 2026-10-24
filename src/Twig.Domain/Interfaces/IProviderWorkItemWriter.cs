using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The provider mutation boundary (Epic #1167, Feature #1178). Update, state, note, link,
/// pending-change flush and proposal batch apply depend on this contract rather than on a
/// concrete provider client, and production composition resolves it from the workspace's
/// selected provider.
/// </summary>
/// <remarks>
/// <para>
/// The members are the field-patch, comment and link half of <see cref="IAdoWorkItemService"/>,
/// hoisted unchanged so the ADO adapter keeps byte-identical requests and every existing
/// fixture stays valid. Create, delete, artifact links, publish-intent recovery and history
/// stay ADO-only on <see cref="IAdoWorkItemService"/> and fail closed for other providers.
/// </para>
/// <para>
/// 🔴 <b>Compatibility seam, not the final shape.</b> <see cref="PatchAsync"/> still takes
/// ADO's integer <c>expectedRevision</c> and every member takes the local integer id. A
/// neutral opaque revision precondition keyed by the neutral store's item identity (#1168)
/// must replace them <b>before any non-ADO writer is registered</b> (GitHub authoring, #1183).
/// Until then only ADO implements this boundary, and composition refuses every other provider
/// with <c>provider-unavailable</c> before any provider client is constructed.
/// </para>
/// </remarks>
public interface IProviderWorkItemWriter : IProviderWorkItemReader
{
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default);

    Task AddCommentAsync(int id, string text, CancellationToken ct = default);

    Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);

    /// <summary>
    /// Adds a link and records <paramref name="comment"/> in the relation's
    /// <c>attributes.comment</c> field (AB#620).
    /// </summary>
    /// <remarks>
    /// A distinctly NAMED method rather than an overload of <see cref="AddLinkAsync"/>, and the
    /// reason is mechanical rather than stylistic: <c>AddLinkAsync</c> is shipped public API
    /// whose last parameter is optional, and the public-API analyzers reject both ways of
    /// extending it — RS0026 forbids a second overload that also has optional parameters, and
    /// RS0027 forbids the shipped one keeping its optional parameter while a longer overload
    /// exists. Widening the shipped signature in place would instead delete a shipped symbol.
    ///
    /// <para>
    /// The comment is the reason for the relationship, which for a symmetric
    /// <c>System.LinkTypes.Related</c> edge is the valuable half — an unexplained related link
    /// is an assertion with no argument. ADO carries it on the relation itself, so it must be
    /// set at creation; a create-then-comment pair would write a work item COMMENT instead,
    /// which is a different thing in a different place.
    /// </para>
    ///
    /// <para>
    /// A null or whitespace <paramref name="comment"/> must emit no <c>attributes</c> object at
    /// all, so this sends byte-identical JSON to <see cref="AddLinkAsync"/> in that case. That
    /// is what lets callers route every add through here without changing existing requests.
    /// </para>
    /// </remarks>
    Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default);

    Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);
}
