using System.Security.Cryptography;
using System.Text.Json;

namespace Bench.Domain;

public static class SourceFingerprint
{
    public static string Binding(SourceBinding binding) => Hash(Write(w => WriteBinding(w, binding)));

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
    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return buffer.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
