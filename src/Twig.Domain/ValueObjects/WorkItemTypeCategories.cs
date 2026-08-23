namespace Twig.Domain.ValueObjects;

/// <summary>
/// Reference names of the work item type categories twig reasons about, from
/// <c>_apis/wit/workitemtypecategories</c> (AB#656).
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is NOT a list of hidden type NAMES, and must never become one.</b> A category
/// reference name is part of ADO's own vocabulary and is stable across every process; a list
/// of the types that happen to be in a category is process-specific and rots. The measured
/// Hyperbright process puts ten types in <see cref="Hidden"/> and a customer process will put
/// different ones there — which is precisely why membership is read from the route rather than
/// assumed here.
/// </para>
/// <para>
/// Only the categories twig actually reasons about are named. The rest are still carried
/// verbatim on <see cref="Aggregates.ProcessTypeRecord.CategoryReferenceNames"/>: an unknown
/// category is data to pass through, not data to drop.
/// </para>
/// </remarks>
public static class WorkItemTypeCategories
{
    /// <summary>
    /// The category ADO uses for types it reserves for its own tooling — the authority for
    /// "a user must not create one of these manually".
    /// </summary>
    public const string Hidden = "Microsoft.HiddenCategory";
}
