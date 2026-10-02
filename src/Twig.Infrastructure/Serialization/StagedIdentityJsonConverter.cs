using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

/// <summary>Durable receipt identities use their canonical GUID form, never an unconstructible value-object shape.</summary>
internal sealed class StagedIdentityJsonConverter : JsonConverter<StagedIdentity>
{
    public override StagedIdentity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && StagedIdentity.TryParse(reader.GetString(), out var identity)
            ? identity : throw new JsonException("Invalid staged identity in native outcome receipt.");

    public override void Write(Utf8JsonWriter writer, StagedIdentity value, JsonSerializerOptions options)
    {
        if (value.IsEmpty) throw new JsonException("Native outcome receipts cannot contain an empty staged identity.");
        writer.WriteStringValue(value.ToString());
    }
}
