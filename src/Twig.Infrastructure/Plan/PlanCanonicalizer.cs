using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Produces the canonical byte form of a plan (or subtree) and its SHA-256 digest.
/// Canonical form is the whole point of digests being comparable: two files that differ
/// only in whitespace or property order must reduce to the same bytes.
/// <para>
/// Rules (matching the shared contract):
/// </para>
/// <list type="bullet">
///   <item>Objects: property names sorted by ordinal ascending; every property emitted once.</item>
///   <item>Arrays: order preserved verbatim.</item>
///   <item>Values: numbers/strings/booleans/null re-emitted from the parsed JsonElement — never
///     the raw source text — so leading whitespace and comments cannot survive.</item>
///   <item>Compact: no whitespace between tokens.</item>
///   <item>Digest: lowercase-hex SHA-256 of the canonical UTF-8 bytes.</item>
/// </list>
/// <para>
/// AOT-safe: uses only <see cref="JsonDocument"/> and <see cref="Utf8JsonWriter"/>; no
/// reflection-based (de)serialization is performed.
/// </para>
/// </summary>
public static class PlanCanonicalizer
{
    /// <summary>
    /// Writes the canonical form of <paramref name="element"/> to a fresh UTF-8 buffer
    /// and returns it as a string alongside the lowercase-hex SHA-256 digest.
    /// </summary>
    public static (string CanonicalJson, string Digest) Canonicalize(JsonElement element)
    {
        var bytes = CanonicalizeToUtf8(element);
        var digest = ComputeDigest(bytes);
        return (Encoding.UTF8.GetString(bytes), digest);
    }

    /// <summary>
    /// Canonical bytes without the digest — used when the caller only needs the per-op
    /// request JSON stored in the journal.
    /// </summary>
    public static string CanonicalizeToString(JsonElement element)
    {
        var bytes = CanonicalizeToUtf8(element);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Lowercase-hex SHA-256 of an already-canonical UTF-8 byte sequence.</summary>
    public static string ComputeDigest(ReadOnlySpan<byte> canonical)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(canonical, hash);
        return Convert.ToHexStringLower(hash);
    }

    private static byte[] CanonicalizeToUtf8(JsonElement element)
    {
        using var buffer = new ArrayPoolBufferWriter();
        // Disable escaping-of-html to keep output stable and predictable; the default
        // encoder still escapes control chars, which is what we want for determinism.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteCanonical(element, writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Sort by ordinal name — the contract says "sort by property name" —
                // and, in the same pass, reject collisions. JsonDocument silently keeps
                // the last colliding value; if we honoured that we would emit both under
                // the same key (invalid canonical JSON) or hide a distinct authoring
                // behind the same digest. Neither is acceptable for the seam that binds
                // plan file to journal, so we throw. Callers that route through
                // PlanDocumentParser get a structured PlanValidationCodes.DuplicateProperty
                // instead; direct callers of the canonicalizer see this exception.
                var props = new List<JsonProperty>();
                foreach (var p in element.EnumerateObject())
                {
                    props.Add(p);
                }
                props.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                for (var i = 1; i < props.Count; i++)
                {
                    if (string.Equals(props[i].Name, props[i - 1].Name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Cannot canonicalize an object with duplicate property '{props[i].Name}'.");
                    }
                }
                foreach (var p in props)
                {
                    writer.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                // Preserve the number's syntactic form (e.g. 1 vs 1.0). The contract
                // does not require numeric normalization, and reserializing through
                // int/double would lose precision for large integers.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new InvalidOperationException(
                    $"Cannot canonicalize a JsonElement of kind {element.ValueKind}.");
        }
    }

    /// <summary>
    /// Trivial pooled buffer writer — avoids allocating the intermediate MemoryStream
    /// that <see cref="Utf8JsonWriter"/> would otherwise need.
    /// </summary>
    private sealed class ArrayPoolBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 1;
            var needed = _written + sizeHint;
            if (needed <= _buffer.Length) return;
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
            }
        }
    }
}
