using System.Text.Json;
using Twig.Core;

namespace Twig.Store.Sqlite;

/// <summary>One AOT-safe representation for saved Benches and their typed selectors.</summary>
public static class BenchCodec
{
    public static string Serialize(Bench bench)
        => JsonSerializer.Serialize(BenchData.From(bench), TwigJsonContext.Default.BenchData);

    public static Bench Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        Persistence.CheckDuplicates(document.RootElement);
        return (document.Deserialize(TwigJsonContext.Default.BenchData)
            ?? throw new JsonException("Missing Bench definition.")).ToCore();
    }

    public static string SerializeSelectors(IEnumerable<Selector> selectors)
        => JsonSerializer.Serialize(selectors.Select(SelectorData.From).ToArray(), TwigJsonContext.Default.SelectorDataArray);

    public static IReadOnlyList<Selector> DeserializeSelectors(string json)
    {
        using var document = JsonDocument.Parse(json);
        Persistence.CheckDuplicates(document.RootElement);
        return (document.Deserialize(TwigJsonContext.Default.SelectorDataArray)
            ?? throw new JsonException("Missing selectors.")).Select(s => s.ToCore()).ToArray();
    }
}
