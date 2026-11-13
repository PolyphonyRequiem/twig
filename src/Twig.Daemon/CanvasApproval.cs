using System.Collections.Immutable;
using System.Text.Json;
using Bench.Domain;

namespace Bench.Server;

// Operator-supplied display consent for one immutable source/definition, not provider authorization.
public sealed record CanvasApproval
{
    public CanvasApproval(Guid catalogId, Guid sourceId, SourceBinding binding, Guid benchId, string benchFingerprint)
    {
        CatalogId = Rules.Id(catalogId);
        SourceId = Rules.Id(sourceId);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        BenchId = Rules.Id(benchId);
        if (benchFingerprint is not { Length: 64 } || benchFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An exact frozen Bench fingerprint is required.");
        BenchFingerprint = benchFingerprint;
    }

    public Guid CatalogId { get; }
    public Guid SourceId { get; }
    public SourceBinding Binding { get; }
    public Guid BenchId { get; }
    public string BenchFingerprint { get; }
    public string ToOperatorJson() => JsonSerializer.Serialize(this, TransportJson.Default.CanvasApproval);
    public static CanvasApproval FromOperatorJson(string json)
    {
        if (json.Length > 16384) throw new InvalidDataException("Approval record exceeds its bound.");
        return JsonSerializer.Deserialize(json, TransportJson.Default.CanvasApproval)
            ?? throw new InvalidDataException("An exact source-bound approval record is required.");
    }

    public bool Matches(CatalogSnapshot catalog)
    {
        if (catalog.Id != CatalogId || catalog.Sources.SingleOrDefault(s => s.Id == SourceId)?.Binding != Binding)
            return false;
        var bench = catalog.Benches.SingleOrDefault(b => b.Id == BenchId);
        return bench is not null && bench.Sources.Length == 1
            && bench.Sources[0].SourceId == SourceId && bench.Sources[0].Binding == Binding
            && CatalogFingerprint.Definition(bench) == BenchFingerprint;
    }

    internal CatalogSnapshot Filter(CatalogSnapshot catalog)
    {
        if (!Matches(catalog)) throw new HttpFailure(403, "CanvasScopeChanged", "Approved source or Bench identity changed. Pair a newly approved view.");
        return catalog with
        {
            Sources = catalog.Sources.Where(s => s.Id == SourceId).ToImmutableArray(),
            Benches = catalog.Benches.Where(b => b.Id == BenchId).ToImmutableArray(),
        };
    }

    internal ViewPreferences Restrict(ViewPreferences value)
    {
        var group = value.GroupSource == SourceId && value.GroupField is { Length: > 0 and <= 256 } field
            && !field.Any(char.IsControl);
        return value with { BenchId = BenchId, GroupSource = group ? SourceId : null, GroupField = group ? value.GroupField : null };
    }

    internal bool Permits(ViewPreferences value) => (value.BenchId is null || value.BenchId == BenchId)
        && (value.GroupSource is null || value.GroupSource == SourceId);
}
