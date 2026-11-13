using System.Security.Cryptography;
using System.Text.Json;
using Twig.Core;

namespace Bench.Domain;

public static class CatalogFingerprint
{
    public static string Creation(Guid catalogId, BenchDefinition definition) => Hash(Write(w =>
    {
        w.WriteStartArray();
        w.WriteNumberValue(1);
        w.WriteStringValue(Rules.Id(catalogId));
        w.WriteStringValue(Definition(definition));
        w.WriteEndArray();
    }));
    public static string Binding(SourceBinding binding) => Hash(Write(w => WriteBinding(w, binding)));
    public static string Definition(BenchDefinition bench) => Hash(Write(w =>
    {
        w.WriteStartArray();
        w.WriteNumberValue(1);
        w.WriteStringValue(bench.Id);
        w.WriteStringValue(bench.Name);
        w.WriteStartArray();
        foreach (var source in bench.Sources)
        {
            w.WriteStartArray();
            w.WriteStringValue(source.SourceId);
            WriteBinding(w, source.Binding);
            w.WriteStartArray();
            foreach (var selector in source.Selectors)
            {
                w.WriteStartArray();
                w.WriteNumberValue((int)selector.Kind);
                if (selector.Item is null) w.WriteNullValue(); else WriteKey(w, selector.Item);
                w.WriteStringValue(selector.HierarchyRelation);
                if (selector.Query is null) w.WriteNullValue();
                else w.WriteRawValue(Twig.Core.Core.QueryBytes(selector.Query));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndArray();
    }));

    private static void WriteBinding(Utf8JsonWriter w, SourceBinding value)
    {
        w.WriteStartArray();
        w.WriteStringValue(value.Connection.Provider.Value);
        w.WriteStringValue(value.Connection.ResourceScope);
        w.WriteStringValue(value.Connection.Destination);
        w.WriteStringValue(value.Connection.IdentityRef);
        w.WriteStringValue(value.StoreInstance);
        w.WriteStringValue(value.NativeStamp);
        w.WriteEndArray();
    }
    private static void WriteKey(Utf8JsonWriter w, ItemKey key)
    {
        w.WriteStartArray();
        if (key.IsLocal) { w.WriteStringValue("local"); w.WriteStringValue(key.LocalId); }
        else
        {
            w.WriteStringValue("remote");
            w.WriteStringValue(key.Provider!.Value);
            w.WriteStringValue(key.ResourceScope);
            w.WriteStringValue(key.NativeId);
        }
        w.WriteEndArray();
    }
    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return buffer.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
