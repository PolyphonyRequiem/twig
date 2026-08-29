namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// Response from GET /{project}/_apis/wit/fields (list envelope).
/// </summary>
internal sealed class AdoFieldListResponse
{
    public int Count { get; set; }
    public List<AdoFieldResponse>? Value { get; set; }
}

/// <summary>
/// A single field definition from the ADO fields API.
/// </summary>
internal sealed class AdoFieldResponse
{
    public string? ReferenceName { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool ReadOnly { get; set; }

    /// <summary>
    /// 🔴 The only server-stated way to tell an identity field from an ordinary string —
    /// ADO reports both with <c>type: "string"</c> (AB#802).
    /// </summary>
    /// <remarks>
    /// Non-nullable on purpose, unlike <c>isPicklist</c> on the org-scoped route. ADO omits
    /// this key rather than sending <c>false</c>, so absent deserializes to <c>false</c> —
    /// and here that default is the safe one, because a field not recognised as an identity
    /// simply keeps ordinal readback comparison. Nothing consumes the negative as a stated
    /// server fact, so there is no claim to weaken by conflating absent with false.
    /// </remarks>
    public bool IsIdentity { get; set; }
}
