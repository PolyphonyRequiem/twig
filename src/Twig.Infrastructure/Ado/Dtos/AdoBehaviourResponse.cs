using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// Wire shape for
/// <c>GET /_apis/work/processes/{processId}/workItemTypesBehaviors/{witRefName}/behaviors</c> —
/// which backlog levels ONE type belongs to.
/// </summary>
/// <remarks>
/// 🔴 <b>The route segment is <c>workItemTypesBehaviors</c>, not
/// <c>workItemTypes/{ref}/behaviors</c>.</b> The latter returns an HTML 404 ("the controller
/// for path … was not found") for every type on both an inherited and a stock process —
/// verified live 2026-08-11 and re-verified 2026-08-12. It is the obvious route and it does
/// not exist.
/// <para>
/// The rows carry a REFERENCE only: <c>{"behavior":{"id":"Custom.3daa…"},"isDefault":true}</c>.
/// Naming the level costs a second, process-scoped call — see
/// <see cref="AdoProcessBehaviourListResponse"/>.
/// </para>
/// </remarks>
internal sealed class AdoTypeBehaviourListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoTypeBehaviourResponse>? Value { get; set; }
}

internal sealed class AdoTypeBehaviourResponse
{
    /// <summary>The behaviour this type belongs to, by reference. Names nothing on its own.</summary>
    [JsonPropertyName("behavior")]
    public AdoBehaviourReferenceResponse? Behavior { get; set; }

    /// <summary>
    /// Whether this is the type's DEFAULT level — where a new item of this type lands.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

internal sealed class AdoBehaviourReferenceResponse
{
    /// <summary>
    /// The behaviour's reference name, e.g. <c>Microsoft.VSTS.Basic.EpicBacklogBehavior</c> or
    /// <c>Custom.3daa3b35-…</c>. 🔴 Keyed <c>id</c> on this route while the catalogue route
    /// keys the same value <c>referenceName</c>; they are the same identity and the join
    /// between them is the whole reason the catalogue is fetched.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>
/// Wire shape for <c>GET /_apis/work/processes/{processId}/behaviors</c> — the process's
/// behaviour CATALOGUE, which is what turns a membership reference into a readable name.
/// </summary>
/// <remarks>
/// Process-scoped, so it is one call per run rather than one per type.
/// </remarks>
internal sealed class AdoProcessBehaviourListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoProcessBehaviourResponse>? Value { get; set; }
}

internal sealed class AdoProcessBehaviourResponse
{
    /// <summary>The stable identity, and the join key against the membership route's <c>id</c>.</summary>
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    /// <summary>The display name, e.g. <c>Wayfinding</c>, <c>Epics</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Where the level sits in the backlog hierarchy.</summary>
    [JsonPropertyName("rank")]
    public int? Rank { get; set; }
}
