using System.Security.Cryptography;
using System.Text;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Computes a deterministic hash of a set of field definitions.
/// Used to detect when a process template's field schema has changed.
/// </summary>
public static class FieldDefinitionHasher
{
    /// <summary>
    /// Produces a <c>sha256:&lt;hex&gt;</c> hash that is independent of input order.
    /// Fields are sorted by <see cref="FieldDefinition.ReferenceName"/> before hashing.
    /// </summary>
    public static string ComputeFieldHash(IReadOnlyList<FieldDefinition> fields)
    {
        var sorted = fields.OrderBy(f => f.ReferenceName, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var field in sorted)
            sb.Append($"{field.ReferenceName}:{field.DataType}\n");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
