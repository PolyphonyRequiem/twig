using System.Globalization;
using System.Text.Json;
using Twig.Core;

namespace Twig.Infrastructure.Services;

internal static class CoreProposalFiles
{
    internal static async Task<Result<Proposal>> ReadAsync(string root, string file, CancellationToken ct)
    {
        try
        {
            var path = Path.GetFullPath(file, root);
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Result<Proposal>.Fail(ErrorCode.InvalidInput, "Proposal files must remain inside the explicit workspace root.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024)
                return Result<Proposal>.Fail(ErrorCode.InvalidInput, "The proposal exceeds the 8 MiB input bound.");
            var rootElement = await JsonSerializer.DeserializeAsync(stream, CoreWorkspaceJsonContext.Default.JsonElement, ct);
            return Result<Proposal>.Ok(Parse(rootElement));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return Result<Proposal>.Fail(ErrorCode.InvalidInput, "The proposal is invalid or uses an unsupported canonical format: " + ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result<Proposal>.Fail(ErrorCode.Unavailable, "The explicit proposal file could not be read."); }
    }

    internal static Proposal Parse(JsonElement json)
    {
        Object(json, "version", "connection", "schema", "storeVersion", "policyIdentity", "policyRevision", "operations");
        if (json.GetProperty("version").GetInt32() != Proposal.FormatVersion)
            throw new JsonException("Earlier proposal bytes are preserved, never translated into a new digest.");
        var bound = json.GetProperty("connection");
        Object(bound, "provider", "scope", "destination", "identity");
        var connection = new Connection(new(Text(bound.GetProperty("provider"))), Text(bound.GetProperty("scope")),
            Text(bound.GetProperty("destination")), Text(bound.GetProperty("identity")));
        var operations = json.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array) throw new JsonException("Operations must be an ordered array.");
        return new(connection, Text(json.GetProperty("schema")), json.GetProperty("storeVersion").GetInt64(),
            operations.EnumerateArray().Select(Operation), NullableText(json.GetProperty("policyIdentity")),
            NullableText(json.GetProperty("policyRevision")));
    }

    private static Op Operation(JsonElement json)
    {
        string[] members = ["id", "kind", "item", "revision", "type", "fields", "other", "relation",
            "origin", "concurrency", "localVersion", "body", "resource"];
        var hasLinks = json.TryGetProperty("links", out var creationLinks);
        Object(json, hasLinks ? [.. members, "links"] : members);
        var key = Key(json.GetProperty("item"));
        var revision = json.GetProperty("revision");
        Revision? expected = null;
        if (revision.ValueKind != JsonValueKind.Null)
        {
            Object(revision, "item", "token");
            expected = new(Key(revision.GetProperty("item")), Text(revision.GetProperty("token")));
        }
        var fields = json.GetProperty("fields");
        if (fields.ValueKind != JsonValueKind.Object) throw new JsonException("Fields must be a typed map.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<KeyValuePair<string, FieldValue>>();
        foreach (var property in fields.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JsonException("Duplicate field identity.");
            values.Add(KeyValuePair.Create(property.Name, Value(property.Value)));
        }
        var other = json.GetProperty("other");
        var body = json.GetProperty("body");
        var local = json.GetProperty("localVersion");
        var resource = json.GetProperty("resource");
        ResourceLink? link = null;
        if (resource.ValueKind != JsonValueKind.Null)
        {
            Array(resource, 4);
            link = new(Text(resource[0]), Text(resource[1]), NullableText(resource[2]), NullableText(resource[3]));
        }
        var links = new List<CreationLink>();
        if (hasLinks)
        {
            if (creationLinks.ValueKind != JsonValueKind.Array || creationLinks.GetArrayLength() == 0)
                throw new JsonException("Optional creation links must be a nonempty array.");
            foreach (var value in creationLinks.EnumerateArray())
            {
                Array(value, 3);
                links.Add(new(Key(value[0]), Text(value[1]), value[2].GetBoolean()));
            }
            if (links.Distinct().Count() != links.Count) throw new JsonException("Duplicate creation links.");
        }
        return new(Text(json.GetProperty("id")), Defined<OpKind>(json.GetProperty("kind")), key, expected,
            new(values), NullableText(json.GetProperty("type")), other.ValueKind == JsonValueKind.Null ? null : Key(other),
            NullableText(json.GetProperty("relation")), Defined<RelationshipOrigin>(json.GetProperty("origin")),
            body.ValueKind == JsonValueKind.Null ? null : Value(body), Defined<ConcurrencyMode>(json.GetProperty("concurrency")),
            local.ValueKind == JsonValueKind.Null ? null : local.GetInt64(), link, links);
    }

    private static ItemKey Key(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0)
            throw new JsonException("An exact item identity array is required.");
        var kind = Text(json[0]);
        if (kind == "local")
        {
            Array(json, 2);
            var text = Text(json[1]);
            if (!Guid.TryParseExact(text, "D", out var id) || text != id.ToString("D")) throw new JsonException("Noncanonical local UUID.");
            return ItemKey.Local(id);
        }
        Array(json, 4);
        if (kind != "remote") throw new JsonException("Unknown item identity kind.");
        return ItemKey.Remote(new(Text(json[1])), Text(json[2]), Text(json[3]));
    }

    private static FieldValue Value(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() < 2) throw new JsonException("A typed field tuple is required.");
        var kind = Defined<FieldKind>(json[0]);
        Array(json, kind == FieldKind.Raw ? 3 : 2);
        return kind switch
        {
            FieldKind.Null when json[1].ValueKind == JsonValueKind.Null => FieldValue.Null,
            FieldKind.Text => FieldValue.String(Text(json[1])),
            FieldKind.Html or FieldKind.Markdown => FieldValue.RichText(Text(json[1]), kind),
            FieldKind.Identity => FieldValue.Identity(Text(json[1])),
            FieldKind.Integer => FieldValue.Integer(json[1].GetInt64()),
            FieldKind.Decimal => FieldValue.Decimal(decimal.Parse(Text(json[1]), NumberStyles.Float, CultureInfo.InvariantCulture)),
            FieldKind.Boolean => FieldValue.Bool(json[1].GetBoolean()),
            FieldKind.Instant => FieldValue.At(DateTimeOffset.ParseExact(Text(json[1]), "O", CultureInfo.InvariantCulture)),
            FieldKind.StringList => FieldValue.Strings(json[1].EnumerateArray().Select(Text)),
            FieldKind.Raw => FieldValue.Raw(Text(json[2]), Text(json[1])),
            _ => throw new JsonException("Unsupported field payload."),
        };
    }

    private static void Object(JsonElement json, params string[] members)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new JsonException("An object was required.");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
            if (!actual.Add(property.Name)) throw new JsonException("Duplicate object member.");
        if (!actual.SetEquals(members)) throw new JsonException("Missing or unknown object member.");
    }

    private static void Array(JsonElement json, int length)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() != length)
            throw new JsonException("Unexpected tuple shape.");
    }

    private static string Text(JsonElement json) => json.ValueKind == JsonValueKind.String && json.GetString() is { } value
        ? value : throw new JsonException("A string was required.");
    private static string? NullableText(JsonElement json) => json.ValueKind == JsonValueKind.Null ? null : Text(json);
    private static T Defined<T>(JsonElement json) where T : struct, Enum
    {
        var number = json.GetInt32();
        foreach (var value in Enum.GetValues<T>())
            if (Convert.ToInt32(value, CultureInfo.InvariantCulture) == number) return value;
        throw new JsonException("Unknown enum identity.");
    }
}
