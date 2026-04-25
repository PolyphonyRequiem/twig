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
}
