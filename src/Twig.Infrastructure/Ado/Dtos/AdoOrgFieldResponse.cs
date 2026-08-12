using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// The ORG-scoped field list — <c>_apis/wit/fields</c> at <c>7.1</c> — read for the ONE
/// attribute no process route carries: whether a field is picklist-backed.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is not the type's field list and must never be presented as one.</b> The route
/// is project/org-wide and returns the same rows for every work item type — the founding
/// correctness defect this whole feature exists to fix. It is read here for
/// <see cref="AdoOrgFieldResponse.IsPicklist"/> and
/// <see cref="AdoOrgFieldResponse.PicklistId"/> ONLY, and joined onto the type-scoped list by
/// reference name.
/// </para>
/// <para>
/// 🔴 <c>picklistId</c> is a <b>conditional key</b>: the server omits it entirely rather than
/// sending <c>null</c> when <c>isPicklist</c> is false. So absence of the key is not evidence
/// of anything on its own — <c>isPicklist</c> is the attribute that carries the explicit
/// negative, and it is present on every row. Reading only <c>picklistId</c> is how the
/// original endpoint survey concluded this route did not carry the association at all.
/// </para>
/// <para>
/// Evidence: branch <c>docs/process-descriptor-map</c>,
/// <c>wayfinder-process-descriptor/assets/0005-picklist-association-findings.md</c>.
/// Governing ruling: <c>docs/specs/process-description.spec.md</c> Implementation Decision
/// 5(b).
/// </para>
/// </remarks>
internal sealed class AdoOrgFieldListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoOrgFieldResponse>? Value { get; set; }
}

internal sealed class AdoOrgFieldResponse
{
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    /// <summary>
    /// 🔴 The explicit negative. Present on EVERY row, which is what lets the document state
    /// "not list-constrained" as a server fact rather than as a guess from the field's name.
    /// </summary>
    /// <remarks>
    /// 🔴 NULLABLE deliberately. A non-nullable <c>bool</c> would deserialize an ABSENT key to
    /// <c>false</c>, and <c>false</c> is consumed as a stated server FACT — so a version drift
    /// that dropped the key would silently manufacture the explicit negative out of nothing.
    /// The whole no-heuristic design rests on this key being present; <c>null</c> is how the
    /// code can tell that it was not.
    /// </remarks>
    [JsonPropertyName("isPicklist")]
    public bool? IsPicklist { get; set; }

    /// <summary>
    /// The backing list's id, ABSENT (not null) when <see cref="IsPicklist"/> is false.
    /// </summary>
    [JsonPropertyName("picklistId")]
    public string? PicklistId { get; set; }

    /// <summary>
    /// Whether the backing list is merely SUGGESTED rather than enforced.
    /// </summary>
    /// <remarks>
    /// 🔴 Load-bearing, not decoration. A suggested picklist offers its values in the web
    /// editor while the server still accepts anything — so a field with
    /// <c>isPicklist: true, isPicklistSuggested: true</c> is NOT list-constrained, and
    /// reporting it as such would tell a caller its value must come from the list when a write
    /// of anything else succeeds. That is the overstatement AB#237 exists to remove, arriving
    /// through an unread flag rather than through a bad guess.
    /// <para>
    /// Nullable for the same reason as <see cref="IsPicklist"/>: an absent key must not
    /// silently assert "enforced".
    /// </para>
    /// </remarks>
    [JsonPropertyName("isPicklistSuggested")]
    public bool? IsPicklistSuggested { get; set; }
}

/// <summary>
/// One picklist's contents — <c>_apis/work/processes/lists/{listId}</c> at
/// <c>7.1-preview.1</c>.
/// </summary>
/// <remarks>
/// 🔴 The list-ALL route returns metadata only: every entry carries <c>items: []</c>. So the
/// values cost one call per DISTINCT list, and there is no batch form. Distinct rather than
/// per field, because several fields may share one list.
/// </remarks>
internal sealed class AdoPicklistResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The list's mirror of the field's suggested flag.
    /// </summary>
    /// <remarks>
    /// 🔴 Read as a SECOND witness to the same fact. The field row's
    /// <c>isPicklistSuggested</c> is the primary source; if either side says the list is only
    /// suggested, it is treated as suggested — because "the editor offers these" is the weaker
    /// and therefore safer claim, and disagreement between two views of one list is not
    /// grounds for asserting the stronger one.
    /// </remarks>
    [JsonPropertyName("isSuggested")]
    public bool? IsSuggested { get; set; }

    /// <summary>
    /// The list's values, in the order whoever authored the list happened to type them. 🔴 The
    /// ASSEMBLER sorts these; nothing here may, or byte-stability would depend on two
    /// orderings agreeing forever.
    /// </summary>
    [JsonPropertyName("items")]
    public List<string>? Items { get; set; }
}
