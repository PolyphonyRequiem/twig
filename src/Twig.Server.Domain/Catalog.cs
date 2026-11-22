using System.Collections.Immutable;
using System.Text;
using Twig.Core;

namespace Bench.Domain;

public sealed record SourceBinding
{
    public SourceBinding(Connection connection, Guid storeInstance, string nativeStamp)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (storeInstance == Guid.Empty) throw new ArgumentException("A native store instance is required.");
        StoreInstance = storeInstance;
        NativeStamp = Rules.Text(nativeStamp, 2048);
    }

    public Connection Connection { get; }
    public Guid StoreInstance { get; }
    // Opaque native admission/actor/authority generation. Never a cache revision or credential.
    public string NativeStamp { get; }
}

public sealed record RegisteredSource
{
    public RegisteredSource(Guid id, string label, string nativeLocator, SourceBinding binding,
        bool allowOffline, DateTimeOffset admittedOnlineAt)
    {
        Id = Rules.Id(id);
        Label = Rules.Text(label, 120);
        NativeLocator = Rules.Text(nativeLocator, 4096);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        AllowOffline = allowOffline;
        if (admittedOnlineAt == default) throw new ArgumentException("Successful online admission must be recorded.");
        AdmittedOnlineAt = admittedOnlineAt;
    }
    public Guid Id { get; }
    public string Label { get; }
    public string NativeLocator { get; }
    public SourceBinding Binding { get; }
    public bool AllowOffline { get; }
    public DateTimeOffset AdmittedOnlineAt { get; }
}

public sealed record SourceSelection
{
    public SourceSelection(Guid sourceId, SourceBinding binding, IEnumerable<Selector> selectors)
    {
        SourceId = Rules.Id(sourceId);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentNullException.ThrowIfNull(selectors);
        Selectors = selectors.ToImmutableArray();
        if (Selectors.Length > 1024 || Selectors.Any(s => s is null))
            throw new ArgumentException("A source projection supports at most 1024 non-null selectors.");
        var budget = 8192;
        foreach (var selector in Selectors)
        {
            if (selector.Item is { IsLocal: false } key && !key.BelongsTo(binding.Connection))
                throw new ArgumentException("A selector belongs to this exact source connection.");
            if (selector.Query is not { } query) continue;
            var pending = new Stack<(Query Query, int Depth)>();
            pending.Push((query, 1));
            while (pending.TryPop(out var entry))
            {
                if (--budget < 0 || entry.Depth > 16)
                    throw new ArgumentException("The selected query exceeds the bounded catalog representation.");
                foreach (var child in entry.Query.Children) pending.Push((child, entry.Depth + 1));
            }
        }
    }
    public Guid SourceId { get; }
    public SourceBinding Binding { get; }
    public ImmutableArray<Selector> Selectors { get; }
}

public sealed record BenchDefinition
{
    public BenchDefinition(Guid id, string name, long revision, IEnumerable<SourceSelection> sources)
    {
        Id = Rules.Id(id);
        Name = Rules.Name(name);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        ArgumentNullException.ThrowIfNull(sources);
        var captured = sources.ToImmutableArray();
        if (captured.Length > 64 || captured.Any(s => s is null)
            || captured.Select(s => s.SourceId).Distinct().Count() != captured.Length)
            throw new ArgumentException("A Bench supports at most 64 distinct registered sources.");
        Sources = captured.OrderBy(s => s.SourceId).ToImmutableArray();
    }
    public Guid Id { get; }
    public string Name { get; }
    public string CanonicalName => Rules.CanonicalName(Name);
    public long Revision { get; }
    public ImmutableArray<SourceSelection> Sources { get; }
}

public sealed record CreateBenchCommand
{
    public CreateBenchCommand(Guid id, string name, Guid expectedCatalogId, long expectedCatalogRevision, IEnumerable<SourceSelection> sources)
    {
        if (expectedCatalogRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedCatalogRevision));
        ExpectedCatalogId = Rules.Id(expectedCatalogId);
        Definition = new(id, name, 1, sources);
        ExpectedCatalogRevision = expectedCatalogRevision;
    }
    public BenchDefinition Definition { get; }
    public Guid ExpectedCatalogId { get; }
    public long ExpectedCatalogRevision { get; }
    public string Fingerprint => CatalogFingerprint.Creation(ExpectedCatalogId, Definition);
}

public sealed record CreationReceipt(Guid CatalogId, Guid CommandId, string Fingerprint, BenchDefinition Definition, long CatalogRevision);
public sealed record CatalogSnapshot(Guid Id, long Revision, ImmutableArray<RegisteredSource> Sources,
    ImmutableArray<BenchDefinition> Benches);

public static class Rules
{
    public static Guid Id(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("A stable UUID is required.");
    public static string Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("Use trimmed nonempty text within the documented length bound.");
        return value;
    }
    public static string Name(string value) => Text(value, 120).Normalize(NormalizationForm.FormC);
    // This exact comparison, not SQLite NOCASE or a per-transport approximation, defines uniqueness.
    public static string CanonicalName(string value) => Name(value).ToUpperInvariant();
}
