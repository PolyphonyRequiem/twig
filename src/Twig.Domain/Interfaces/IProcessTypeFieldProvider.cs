using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads the fields that belong to ONE work item type, from the process-scoped per-type
/// fields route.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Not interchangeable with the project-wide field list.</b> The project-wide list
/// is the same for every type in the project; this one is type-scoped, and the two
/// disagreeing is the point. Fetch layer only — nothing renders this yet.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Implementation
/// Decision 6 (the modern <c>processes</c> API, explicitly pinned).
/// </para>
/// </remarks>
internal interface IProcessTypeFieldProvider
{
    /// <summary>
    /// Gets the fields belonging to <paramref name="workItemTypeName"/> under the process
    /// the current project uses.
    /// </summary>
    /// <param name="workItemTypeName">
    /// The type's DISPLAY name, as a caller would type it. The route is keyed by the
    /// type's reference name; resolving one to the other is this provider's job.
    /// </param>
    /// <returns>
    /// The type's fields, or <c>null</c> when they cannot be determined — an unknown or
    /// disabled work item type, an undetectable process, or a server that does not serve
    /// this route for the process. <c>null</c> and an empty list are different facts and
    /// must not be collapsed: "we could not ask" is not "this type has no fields".
    /// </returns>
    Task<IReadOnlyList<ProcessTypeField>?> GetTypeFieldsAsync(
        string workItemTypeName,
        CancellationToken ct = default);
}
