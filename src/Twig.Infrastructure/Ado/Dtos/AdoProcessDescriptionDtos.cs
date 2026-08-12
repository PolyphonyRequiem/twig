using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// The PROCESS-scoped type list's response —
/// <c>_apis/work/processes/{processId}/workItemTypes</c> at <c>7.1-preview.2</c>.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Not the project-scoped <c>_apis/wit/workitemtypes</c> list.</b> That one reports
/// MORE types than the process has — it includes system helper types (Code Review
/// Request/Response, Feedback Request/Response) the process's own type list does not carry.
/// The process's roster is this route's, and every volume figure uses it.
/// </para>
/// <para>
/// 🔴 <b><c>referenceName</c> and <c>customization</c> exist only at preview.2.</b> The same
/// URL at <c>7.1-preview.1</c> returns <c>id</c> and <c>class</c> instead — so a version
/// slip loses stable identity AND authored-vs-inherited without changing the row count.
/// </para>
/// <para>
/// 🔴 <b>Do not add <c>$expand=all</c> to this route expecting more.</b> Probed live
/// 2026-08-11: <c>$expand=all</c> returns FEWER keys than <c>$expand=states</c> or
/// <c>$expand=behaviors</c> — it silently drops both. Named expands are required; "all"
/// is a trap on this route.
/// </para>
/// </remarks>
internal sealed class AdoProcessWorkItemTypeListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoProcessWorkItemTypeResponse>? Value { get; set; }
}

internal sealed class AdoProcessWorkItemTypeResponse
{
    /// <summary>The type's stable identity. Only present at preview.2.</summary>
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary><c>custom</c> | <c>inherited</c> | <c>system</c>. Only present at preview.2.</summary>
    [JsonPropertyName("customization")]
    public string? Customization { get; set; }

    /// <summary>Parent type reference name when derived, else absent/null.</summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; set; }
}

/// <summary>
/// The process-scoped per-type STATES route's response —
/// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/states</c> at <c>7.1</c>.
/// </summary>
/// <remarks>
/// 🔴 Unlike its neighbours in this route family, this one is NOT valid at
/// <c>7.1-preview.2</c> — the server rejects that version outright with
/// <c>VssVersionOutOfRangeException</c>. See <c>AdoApiVersions.ProcessWorkItemTypeStates</c>.
/// </remarks>
internal sealed class AdoProcessStateListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoProcessStateResponse>? Value { get; set; }
}

internal sealed class AdoProcessStateResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("stateCategory")]
    public string? StateCategory { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>
    /// <c>custom</c> | <c>inherited</c> | <c>system</c>. This is what the process-scoped
    /// route buys over the project-scoped state list, which carries no such distinction.
    /// </summary>
    [JsonPropertyName("customizationType")]
    public string? CustomizationType { get; set; }

    /// <summary>
    /// Whether the process has hidden an inherited state. Absent means visible; the server
    /// omits the flag on the common case.
    /// </summary>
    [JsonPropertyName("hidden")]
    public bool? Hidden { get; set; }
}

/// <summary>
/// The project-scoped classic <c>wit</c> type list at <c>$expand=all</c> — the only source
/// of state TRANSITIONS.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 Modelled narrowly ON PURPOSE. This response also carries <c>fields</c>,
/// <c>fieldInstances</c> and <c>xmlForm</c>, and none of them are modelled here: the
/// description's field list must come from the PROCESS-scoped per-type route, and reading
/// fields from this project-scoped response would quietly reintroduce a
/// not-type-scoped-enough field list. Only <c>referenceName</c> and <c>transitions</c> are
/// read.
/// </para>
/// <para>
/// See <c>AdoApiVersions.ProjectWorkItemTypesExpanded</c> for why the modern process API
/// cannot serve this and why transitions must not be derived from the state list.
/// </para>
/// </remarks>
internal sealed class AdoWitTypeTransitionsListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoWitTypeTransitionsResponse>? Value { get; set; }
}

internal sealed class AdoWitTypeTransitionsResponse
{
    /// <summary>
    /// The type's stable identity. Present on this classic route, which is what keeps the
    /// description reference-name-keyed even though the route itself speaks project scope.
    /// </summary>
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    /// <summary>
    /// From-state to allowed destinations. 🔴 The EMPTY-STRING key is the initial
    /// transition — what state a newly created work item enters — and is a real fact, not a
    /// malformed row.
    /// </summary>
    [JsonPropertyName("transitions")]
    public Dictionary<string, List<AdoWitTransitionResponse>>? Transitions { get; set; }
}

internal sealed class AdoWitTransitionResponse
{
    [JsonPropertyName("to")]
    public string? To { get; set; }
}
