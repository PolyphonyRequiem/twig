using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// The per-type fields route's response —
/// <c>_apis/work/processes/{processId}/workItemTypes/{ref}/fields</c> at
/// <c>7.1-preview.2</c>.
/// </summary>
/// <remarks>
/// 🔴 <b>The shape below only exists at preview.2.</b> The identical URL at
/// <c>7.1-preview.1</c> returns <c>description/id/isIdentity/isLocked/name/type/url</c> —
/// no <c>required</c>, no <c>defaultValue</c>, no <c>referenceName</c>, no
/// <c>customization</c> — with identical counts, so a version slip deserializes to rows
/// that are silently blank rather than to an error. The version is named from
/// <c>AdoApiVersions.ProcessWorkItemTypeFields</c> for exactly that reason.
/// <para>
/// The <c>url</c> attribute is deliberately not modelled: on custom rows the server sends
/// a wrong one (it points at <c>…/behaviors</c>). Cosmetic, but nothing may be built on it.
/// </para>
/// </remarks>
internal sealed class AdoProcessTypeFieldListResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoProcessTypeFieldResponse>? Value { get; set; }
}

internal sealed class AdoProcessTypeFieldResponse
{
    [JsonPropertyName("referenceName")]
    public string? ReferenceName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("required")]
    public bool? Required { get; set; }

    [JsonPropertyName("customization")]
    public string? Customization { get; set; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }
}
