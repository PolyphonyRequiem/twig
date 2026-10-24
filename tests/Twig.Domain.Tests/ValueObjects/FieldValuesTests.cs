using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class FieldValuesTests
{
    private static readonly JsonSerializerOptions Options = new();

    private static FieldValues AllKinds() => FieldValues.Create(new KeyValuePair<string, FieldValue>[]
    {
        new("t", new TextValue("hello")),
        new("r", new RichTextValue("<b>x</b>", RichTextFormat.Html)),
        new("m", new RichTextValue("**x**", RichTextFormat.Markdown)),
        new("i", new IntegerValue(long.MinValue)),
        new("d", new DecimalValue(1.50m)),
        new("b", new BooleanValue(false)),
        new("dt", new DateTimeValue(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-7)))),
        new("id", new IdentityValue("Abc-123", "Daniel")),
        new("id2", new IdentityValue("abc-123", null)),
        new("s", new StringListValue(["b", "a", "b"])),
        new("n", NullValue.Instance),
        new("raw", new RawValue("{\"x\":1}", "treePath")),
    });

    private static string Write(FieldValues values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new FieldValuesJsonConverter().Write(writer, values, Options);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static FieldValues Read(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return new FieldValuesJsonConverter().Read(ref reader, typeof(FieldValues), Options);
    }

    [Fact]
    public void EveryKind_RoundTripsToAnEqualMap()
    {
        var original = AllKinds();
        var json = Write(original);

        var restored = Read(json);

        restored.ShouldBe(original);
        Write(restored).ShouldBe(json);
        restored["dt"].Value.ShouldBeOfType<DateTimeValue>().Value.Offset.ShouldBe(TimeSpan.FromHours(-7));
        ((DecimalValue)restored["d"].Value!).Value.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("1.50");
    }

    [Fact]
    public void CanonicalForm_IsOrdinallyKeyedAndKindFirst()
    {
        var values = FieldValues.Empty
            .Set("b", new IntegerValue(2))
            .Set("B", new TextValue("x"))
            .Set("a", NullValue.Instance);

        Write(values).ShouldBe(
            """{"B":{"kind":"text","value":"x"},"a":{"kind":"null"},"b":{"kind":"integer","value":2}}""");
    }

    [Theory]
    [InlineData("""{"f":{"value":"x","kind":"text"}}""")]
    [InlineData("""{"f":{"kind":"Text","value":"x"}}""")]
    [InlineData("""{"f":{"kind":"unknown","value":"x"}}""")]
    [InlineData("""{"f":{"kind":"text","value":"x","extra":1}}""")]
    [InlineData("""{"f":{"kind":"text"}}""")]
    [InlineData("""{"f":{"kind":"integer","value":"1"}}""")]
    [InlineData("""{"f":{"kind":"integer","value":1.5}}""")]
    [InlineData("""{"f":{"kind":"rich-text","format":"HTML","value":"x"}}""")]
    [InlineData("""{"f":{"kind":"rich-text","value":"x","format":"html"}}""")]
    [InlineData("""{"f":{"kind":"date-time","value":"2026-03-04"}}""")]
    [InlineData("""{"f":{"kind":"identity","nativeId":" "}}""")]
    [InlineData("""{"f":{"kind":"string-list","value":["a",null]}}""")]
    [InlineData("""{"f":{"kind":"null","value":null}}""")]
    [InlineData("""{"f":{"kind":"raw","value":"x"}}""")]
    [InlineData("""{"f":{"kind":"text","value":"x"},"f":{"kind":"text","value":"y"}}""")]
    [InlineData("""{" ":{"kind":"text","value":"x"}}""")]
    [InlineData("""{"f":"x"}""")]
    public void NonCanonicalInput_IsRejected(string json)
        => Should.Throw<JsonException>(() => Read(json));

    [Fact]
    public void Create_RejectsBlankDuplicateAndCaselessEntries()
    {
        Should.Throw<ArgumentException>(() => FieldValues.Create([new(" ", new TextValue("x"))]));
        Should.Throw<ArgumentException>(() => FieldValues.Create([new("a", new TextValue("x")), new("a", new TextValue("y"))]));
        Should.Throw<ArgumentException>(() => FieldValues.Create([new("a", default(FieldValue))]));
        Should.Throw<ArgumentException>(() => FieldValues.Empty.Set("a", default));
    }

    [Fact]
    public void Keys_AreOrdinal_AndNullIsDistinctFromAbsent()
    {
        var values = FieldValues.Empty.Set("a", new TextValue("x")).Set("A", NullValue.Instance);

        values.Count.ShouldBe(2);
        values.ContainsKey("missing").ShouldBeFalse();
        values["A"].Value.ShouldBeSameAs(NullValue.Instance);
        values.Remove("a").Remove("A").ShouldBeSameAs(FieldValues.Empty);
    }

    [Fact]
    public void Equality_IsByValue_AndListEqualityIsOrdinalAndOrdered()
    {
        var a = FieldValues.Empty.Set("tags", new StringListValue(["x", "y"]));
        var b = FieldValues.Empty.Set("tags", new StringListValue(new List<string> { "x", "y" }));

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(FieldValues.Empty.Set("tags", new StringListValue(["y", "x"])));
        a.ShouldNotBe(FieldValues.Empty.Set("tags", new StringListValue(["X", "y"])));
        a.ShouldNotBe(FieldValues.Empty.Set("tags", new TextValue("x;y")));
    }

    [Fact]
    public void LocalWorkItemId_IsVersion7_AndRejectsEmpty()
    {
        var id = LocalWorkItemId.New();

        id.Value.Version.ShouldBe(7);
        LocalWorkItemId.TryParse(id.ToString(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
        LocalWorkItemId.TryParse(Guid.Empty.ToString(), out _).ShouldBeFalse();
        LocalWorkItemId.TryParse("not-a-guid", out _).ShouldBeFalse();
        LocalWorkItemId.TryParse(id.ToString().ToUpperInvariant(), out _).ShouldBeFalse();
        LocalWorkItemId.TryParse(id.Value.ToString("N"), out _).ShouldBeFalse();
        LocalWorkItemId.TryParse(id.Value.ToString("B"), out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => LocalWorkItemId.FromGuid(Guid.Empty));
        default(LocalWorkItemId).IsEmpty.ShouldBeTrue();
    }
}
