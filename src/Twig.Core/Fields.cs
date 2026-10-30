using System.Collections;
using System.Collections.Immutable;

namespace Twig.Core;

public enum FieldKind { Null, Text, Html, Markdown, Integer, Decimal, Boolean, Instant, Identity, StringList, Raw }

public sealed class FieldValue : IEquatable<FieldValue>
{
    private FieldValue(FieldKind kind, string? text = null, decimal number = 0, bool boolean = false,
        DateTimeOffset instant = default, ImmutableArray<string> values = default, string? dataType = null)
        => (Kind, Text, Number, Boolean, Instant, Values, DataType) =
            (kind, text, number, boolean, instant, values.IsDefault ? [] : values, dataType);

    public FieldKind Kind { get; }
    public string? Text { get; }
    public decimal Number { get; }
    public bool Boolean { get; }
    public DateTimeOffset Instant { get; }
    public ImmutableArray<string> Values { get; }
    public string? DataType { get; }
    public static FieldValue Null { get; } = new(FieldKind.Null);
    public static FieldValue String(string value) => new(FieldKind.Text, value ?? throw new ArgumentNullException(nameof(value)));
    public static FieldValue RichText(string value, FieldKind format)
        => format is FieldKind.Html or FieldKind.Markdown
            ? new(format, value ?? throw new ArgumentNullException(nameof(value)))
            : throw new ArgumentException("Rich text must declare HTML or Markdown.", nameof(format));
    public static FieldValue Integer(long value) => new(FieldKind.Integer, number: value);
    public static FieldValue Decimal(decimal value) => new(FieldKind.Decimal, number: value);
    public static FieldValue Bool(bool value) => new(FieldKind.Boolean, boolean: value);
    public static FieldValue At(DateTimeOffset value) => new(FieldKind.Instant, instant: value);
    public static FieldValue Identity(string nativeId) => new(FieldKind.Identity, Contract.Text(nativeId, nameof(nativeId)));
    public static FieldValue Strings(IEnumerable<string> values) => new(FieldKind.StringList, values: Contract.Copy(values));
    public static FieldValue Raw(string value, string dataType)
        => new(FieldKind.Raw, value ?? throw new ArgumentNullException(nameof(value)),
            dataType: Contract.Text(dataType, nameof(dataType)));

    public bool IsEmpty => Kind == FieldKind.Null
        || Kind is FieldKind.Text or FieldKind.Html or FieldKind.Markdown && string.IsNullOrWhiteSpace(Text)
        || Kind == FieldKind.StringList && Values.IsEmpty;

    public bool Equals(FieldValue? other) => other is not null
        && Kind == other.Kind && Text == other.Text && Number == other.Number
        && Boolean == other.Boolean && Instant.EqualsExact(other.Instant) && DataType == other.DataType
        && Values.SequenceEqual(other.Values, StringComparer.Ordinal);
    public override bool Equals(object? obj) => obj is FieldValue value && Equals(value);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind); hash.Add(Text); hash.Add(Number); hash.Add(Boolean);
        hash.Add(Instant.Ticks); hash.Add(Instant.Offset); hash.Add(DataType);
        foreach (var value in Values) hash.Add(value, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class FieldValues : IReadOnlyDictionary<string, FieldValue>, IEquatable<FieldValues>
{
    private readonly ImmutableSortedDictionary<string, FieldValue> _values;

    public FieldValues(IEnumerable<KeyValuePair<string, FieldValue>> values)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, FieldValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            Contract.Text(key, nameof(values));
            ArgumentNullException.ThrowIfNull(value);
            builder.Add(key, value);
        }
        _values = builder.ToImmutable();
    }

    public static FieldValues Empty { get; } = new([]);
    public FieldValues Set(string key, FieldValue value) => new(_values.SetItem(Contract.Text(key, nameof(key)), value));
    public FieldValues Overlay(FieldValues changes) => new(_values.SetItems(changes));
    public FieldValue this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<FieldValue> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out FieldValue value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, FieldValue>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(FieldValues? other) => other is not null && _values.SequenceEqual(other._values);
    public override bool Equals(object? obj) => obj is FieldValues values && Equals(values);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values) { hash.Add(key, StringComparer.Ordinal); hash.Add(value); }
        return hash.ToHashCode();
    }
}
