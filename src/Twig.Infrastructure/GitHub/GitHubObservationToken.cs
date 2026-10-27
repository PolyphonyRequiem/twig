using System.Security.Cryptography;
using System.Text.Json;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>An observation fingerprint, not a server revision or a conditional-write guarantee.</summary>
internal static class GitHubObservationToken
{
    internal static ProviderRevisionToken Create(
        ExternalWorkItemIdentity identity, FieldValues fields, IEnumerable<ProviderWorkItemRelationship> relationships)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            json.WriteStringValue(identity.ResourceScope);
            json.WriteStringValue(identity.NativeId);
            JsonSerializer.Serialize(json, fields, TwigJsonContext.Default.FieldValues);
            foreach (var edge in relationships.OrderBy(e => e.Kind)
                .ThenBy(e => e.Source.NativeId, StringComparer.Ordinal).ThenBy(e => e.Target.NativeId, StringComparer.Ordinal))
            {
                json.WriteStartArray();
                json.WriteStringValue(edge.Kind.ToString());
                json.WriteStringValue(edge.Source.NativeId);
                json.WriteStringValue(edge.Target.NativeId);
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
        return new("github-observed:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant());
    }
}
