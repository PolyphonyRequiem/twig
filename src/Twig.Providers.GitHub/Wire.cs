using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;

namespace Twig.Providers.GitHub;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class TwigJsonContext : JsonSerializerContext;

internal sealed class GitHubFailure(Error error) : Exception(error.Message)
{
    internal Error Error { get; } = error;
}

internal static class Wire
{
    internal static GitHubFailure Refuse(ErrorCode code, string message) => new(new(code, message));
    internal static GitHubFailure Invalid(string message) => Refuse(ErrorCode.Unavailable, message);
    internal static string Id(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim()
        && value.Length <= 256 && !value.Any(char.IsControl) ? value : throw Invalid("Invalid opaque native identity.");
    internal static JsonElement Member(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value : throw Invalid($"Native response did not observe '{name}'.");
    internal static string Text(JsonElement parent, string name)
    {
        var value = Member(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid($"Invalid native '{name}'.");
    }

    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) write(json);
        return buffer.ToArray();
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Utf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    internal static JsonElement Parse(byte[] bytes)
    {
        var root = JsonSerializer.Deserialize(bytes, TwigJsonContext.Default.JsonElement);
        Unique(root);
        return root;
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Duplicate native JSON member.");
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Unique(child);
    }

    internal static byte[] Request(string document, Action<Utf8JsonWriter>? variables = null) => Json(json =>
    {
        json.WriteStartObject(); json.WriteString("query", document);
        json.WriteStartObject("variables"); variables?.Invoke(json); json.WriteEndObject(); json.WriteEndObject();
    });

    internal static JsonElement Data(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors)
            && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw Invalid("GraphQL returned errors or partial data; runtime availability is not established.");
        var data = Member(root, "data");
        return data.ValueKind == JsonValueKind.Object ? data : throw Invalid("GraphQL data is unavailable.");
    }

    internal static async Task<byte[]> Response(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + read > 8 * 1024 * 1024) throw Invalid("Native response exceeds 8 MiB.");
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }
}
