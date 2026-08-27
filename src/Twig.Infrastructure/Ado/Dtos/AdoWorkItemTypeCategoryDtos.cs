using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// The work item type CATEGORIES route's response —
/// <c>_apis/wit/workitemtypecategories</c> (AB#656).
/// </summary>
/// <remarks>
/// <para>
/// This is the authority for which types ADO reserves for its own tooling: membership of
/// <c>Microsoft.HiddenCategory</c>. Neither type list route carries that fact — the
/// project-scoped <c>_apis/wit/workitemtypes</c> exposes only <c>isDisabled</c>, and the
/// process-scoped roster exposes only <c>customization</c>, which is a different question
/// (authored-vs-inherited, not usable-vs-tooling).
/// </para>
/// <para>
/// 🔴 <b>The relation is many-to-many.</b> A type appears under every category it belongs to,
/// so the same name recurs across rows. Measured live on the Hyperbright process: <c>Issue</c>
/// appears under <c>Microsoft.HiddenCategory</c>, <c>Microsoft.BugCategory</c> AND
/// <c>Microsoft.RequirementCategory</c>, while <c>Bug</c> appears under
/// <c>Microsoft.RequirementCategory</c> and NOT <c>Microsoft.BugCategory</c>. Any code that
/// reduces a type to one category, or guesses from the name, is wrong on a real process.
/// </para>
/// <para>
/// Types are matched by <c>name</c> rather than <c>referenceName</c> because the
/// <see cref="Twig.Domain.ValueObjects.WorkItemTypeWithStates"/> the sync path builds is
/// name-keyed throughout, as is the <c>process_types</c> table's primary key.
/// </para>
/// </remarks>
internal sealed class AdoWorkItemTypeCategoryListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoWorkItemTypeCategoryResponse>? Value { get; set; }
}

internal sealed class AdoWorkItemTypeCategoryResponse
{
    /// <summary>The category's stable identity, e.g. <c>Microsoft.HiddenCategory</c>.</summary>
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    /// <summary>Display name, e.g. "Hidden Types Category". Not stable; do not match on it.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The types in this category. A type may appear in several categories.</summary>
    [JsonPropertyName("workItemTypes")]
    public List<AdoWorkItemTypeCategoryMemberResponse>? WorkItemTypes { get; set; }
}

internal sealed class AdoWorkItemTypeCategoryMemberResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }
}
