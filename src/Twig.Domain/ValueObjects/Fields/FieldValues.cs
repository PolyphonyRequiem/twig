using System.Collections;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects.Fields;

/// <summary>The markup a <see cref="RichTextValue"/> carries.</summary>
public enum RichTextFormat
{
    /// <summary>HTML markup.</summary>
    Html,

    /// <summary>Markdown markup.</summary>
    Markdown,
}

/// <summary>A plain string value.</summary>
public sealed record TextValue
{
    /// <summary>Creates a plain string value.</summary>
    public TextValue(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The string.</summary>
    public string Value { get; }
}

/// <summary>Formatted text with its markup made explicit.</summary>
public sealed record RichTextValue
{
    /// <summary>Creates a rich-text value.</summary>
    public RichTextValue(string content, RichTextFormat format)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Format = format is RichTextFormat.Html or RichTextFormat.Markdown
            ? format
            : throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown rich-text format.");
    }

    /// <summary>The marked-up content.</summary>
    public string Content { get; }

    /// <summary>The markup language.</summary>
    public RichTextFormat Format { get; }
}

/// <summary>A 64-bit integer value.</summary>
public sealed record IntegerValue(long Value);

/// <summary>An exact decimal value; scale is preserved.</summary>
public sealed record DecimalValue(decimal Value);

/// <summary>A boolean value.</summary>
public sealed record BooleanValue(bool Value);

/// <summary>An instant with its original offset preserved.</summary>
public sealed record DateTimeValue(DateTimeOffset Value);

/// <summary>A person or group, keyed by the provider's opaque native identifier.</summary>
public sealed record IdentityValue
{
    /// <summary>Creates an identity value.</summary>
    public IdentityValue(string nativeId, string? displayName)
    {
        NativeId = string.IsNullOrWhiteSpace(nativeId)
            ? throw new ArgumentException("An identity value needs a native identifier.", nameof(nativeId))
            : nativeId;
        DisplayName = displayName;
    }

    /// <summary>The provider's opaque, case-preserved identifier.</summary>
    public string NativeId { get; }

    /// <summary>A display name, when one was observed.</summary>
    public string? DisplayName { get; }
}

/// <summary>An ordered list of strings, such as tags. Equality is element-wise and ordinal.</summary>
public sealed record StringListValue
{
    /// <summary>Creates a string list. Null elements are rejected.</summary>
    public StringListValue(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var array = values.ToImmutableArray();
        foreach (var value in array)
        {
            if (value is null)
                throw new ArgumentException("A string list cannot contain null.", nameof(values));
        }

        Values = array;
    }

    /// <summary>The strings, in their original order.</summary>
    public ImmutableArray<string> Values { get; }

    /// <inheritdoc />
    public bool Equals(StringListValue? other)
        => other is not null && Values.AsSpan().SequenceEqual(other.Values.AsSpan(), StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
            hash.Add(value, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>An explicit clear. Distinct from an absent key, which means "not observed or not changed".</summary>
public sealed record NullValue
{
    /// <summary>The single explicit-clear value.</summary>
    public static NullValue Instance { get; } = new();

    private NullValue() { }
}

/// <summary>
/// A value Twig does not model, preserved verbatim with the provider's own data-type name so
/// it round-trips without coercion.
/// </summary>
public sealed record RawValue
{
    /// <summary>Creates a raw value.</summary>
    public RawValue(string raw, string providerDataType)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        ProviderDataType = string.IsNullOrWhiteSpace(providerDataType)
            ? throw new ArgumentException("A raw value needs its provider data type.", nameof(providerDataType))
            : providerDataType;
    }

    /// <summary>The provider's serialized value.</summary>
    public string Raw { get; }

    /// <summary>The provider's own data-type name; opaque to Twig.</summary>
    public string ProviderDataType { get; }
}

/// <summary>
/// A provider-neutral field value (Epic #1168). Closed: every variant is listed here, and the
/// canonical serialized form is written by <see cref="FieldValuesJsonConverter"/>, never by
/// reflection-based polymorphism.
/// </summary>
/// <remarks>
/// Pattern-match the case (<c>value.Value is TextValue t</c>). A default-constructed union
/// carries no case and is rejected wherever a value is stored.
/// </remarks>
public union FieldValue(
    TextValue,
    RichTextValue,
    IntegerValue,
    DecimalValue,
    BooleanValue,
    DateTimeValue,
    IdentityValue,
    StringListValue,
    NullValue,
    RawValue);

/// <summary>
/// An immutable map of field values keyed by the provider's opaque reference name, compared
/// ordinally. A missing key means the field was not observed (or, in a change, not changed);
/// an explicit clear is <see cref="NullValue"/>.
/// </summary>
/// <remarks>
/// Keys are compared and ordered ordinally, so the map has one canonical serialized form.
/// Adapters own any case normalization a provider needs before keys reach this map.
/// </remarks>
[JsonConverter(typeof(FieldValuesJsonConverter))]
public sealed class FieldValues : IReadOnlyDictionary<string, FieldValue>, IEquatable<FieldValues>
{
    private readonly ImmutableSortedDictionary<string, FieldValue> _values;

    private FieldValues(ImmutableSortedDictionary<string, FieldValue> values) => _values = values;

    /// <summary>The empty map.</summary>
    public static FieldValues Empty { get; } = new(ImmutableSortedDictionary.Create<string, FieldValue>(StringComparer.Ordinal));

    /// <summary>
    /// Builds a map. Rejects blank keys, duplicate keys, and values that carry no case.
    /// </summary>
    public static FieldValues Create(IEnumerable<KeyValuePair<string, FieldValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, FieldValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            ValidateEntry(key, value);
            if (builder.ContainsKey(key))
                throw new ArgumentException($"Duplicate field reference name '{key}'.", nameof(values));
            builder.Add(key, value);
        }

        return builder.Count == 0 ? Empty : new FieldValues(builder.ToImmutable());
    }

    /// <summary>Returns a copy with <paramref name="referenceName"/> set.</summary>
    public FieldValues Set(string referenceName, FieldValue value)
    {
        ValidateEntry(referenceName, value);
        return new FieldValues(_values.SetItem(referenceName, value));
    }

    /// <summary>Returns a copy without <paramref name="referenceName"/>.</summary>
    public FieldValues Remove(string referenceName)
    {
        var removed = _values.Remove(referenceName);
        return removed.Count == 0 ? Empty : new FieldValues(removed);
    }

    /// <inheritdoc />
    public FieldValue this[string key] => _values[key];

    /// <inheritdoc />
    public IEnumerable<string> Keys => _values.Keys;

    /// <inheritdoc />
    public IEnumerable<FieldValue> Values => _values.Values;

    /// <inheritdoc />
    public int Count => _values.Count;

    /// <inheritdoc />
    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out FieldValue value) => _values.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, FieldValue>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(FieldValues? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Count != Count) return false;
        foreach (var (key, value) in _values)
        {
            if (!other._values.TryGetValue(key, out var otherValue) || !Equals(value.Value, otherValue.Value))
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldValues);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value.Value);
        }

        return hash.ToHashCode();
    }

    private static void ValidateEntry(string key, FieldValue value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A field reference name cannot be blank.", nameof(key));
        if (value.Value is null)
            throw new ArgumentException($"Field '{key}' carries no value case; use NullValue for an explicit clear.", nameof(value));
    }
}
