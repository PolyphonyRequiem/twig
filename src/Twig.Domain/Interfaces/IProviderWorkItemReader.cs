using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The provider read boundary (Epic #1167, Feature #1177). Every command read path — show,
/// query, refresh and sync — depends on this contract rather than on a concrete provider
/// client, and production composition resolves it from the workspace's selected provider.
/// </summary>
/// <remarks>
/// <para>
/// The members are the read half of <see cref="IAdoWorkItemService"/>, hoisted unchanged so
/// the ADO adapter keeps byte-identical behaviour and every existing fixture stays valid. The
/// patch, comment and link surface is <see cref="IProviderWorkItemWriter"/> (Feature #1178);
/// create, publish, delete and history stay ADO-only on <see cref="IAdoWorkItemService"/>.
/// </para>
/// <para>
/// Reads are keyed by the local integer work-item id the cache uses. A provider whose native
/// identity is not that id (GitHub) cannot satisfy this contract until the neutral store
/// (#1168) maps local to external identity, so composition refuses it with
/// <c>provider-unavailable</c> instead of guessing.
/// </para>
/// <para>
/// <see cref="QueryByWiqlAsync(string, CancellationToken)"/> takes the provider's native query
/// dialect. ADO's dialect is WIQL; the member name is kept for source compatibility.
/// </para>
/// </remarks>
public interface IProviderWorkItemReader
{
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Fetches a SET of work items together with the non-hierarchy edges among them (ADO #154).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This costs no additional ADO requests over <see cref="FetchBatchAsync"/>.</b> The
    /// batch URL already carries <c>$expand=relations</c>, so the relations were always on the
    /// wire; the batch path simply mapped them away. This overload keeps them.
    /// </para>
    /// <para>
    /// Returned links carry every edge sourced from a fetched item, including edges whose target
    /// lies outside the requested set — a consumer discovering what to fetch next needs exactly
    /// those, and filtering them here would make that impossible.
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default);

    // Hoisted verbatim from IAdoWorkItemService, where both overloads have already shipped.
    // Renaming or dropping one to satisfy RS0026 would break every existing caller and mock.
#pragma warning disable RS0026
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default);
#pragma warning restore RS0026
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
}
