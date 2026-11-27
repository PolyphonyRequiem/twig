using System.Collections.Immutable;
using System.Diagnostics;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Twig.Core;
using CoreBench = Twig.Core.Bench;

if (args is ["--commit-without-response", var database, var creationId, var catalogIdentityText])
{
    var reopened = Required(await SqliteCatalog.OpenAsync(database));
    _ = Required(await reopened.CreateAsync(new(Guid.Parse(creationId), "Synthetic crash receipt", Guid.Parse(catalogIdentityText), 0, []), default));
    // Deliberately exit the owned test process after commit without returning any transport response.
    Environment.Exit(23);
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("domain/application dependencies point inward and expose no native write ports", () =>
    {
        foreach (var assembly in new[] { typeof(BenchDefinition).Assembly, typeof(BenchCatalog).Assembly })
        {
            var dependencies = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
            Check(!dependencies.Any(n => n.Contains("AspNet", StringComparison.Ordinal)
                || n.Contains("Sqlite", StringComparison.Ordinal) || n.Contains("Infrastructure", StringComparison.Ordinal)
                || n.Contains("Copilot", StringComparison.Ordinal)), "Outward dependency found.");
        }
        Check(!typeof(ISourceReader).GetMethods().Any(m => m.ReturnType == typeof(IStore) || m.ReturnType == typeof(IProvider)),
            "The application exposed a native writer.");
        return Task.CompletedTask;
    }),
    ("canonical names and stable fingerprint freeze typed selector content, not CAS preconditions", () =>
    {
        Equal(Rules.CanonicalName("Caf\u00e9"), Rules.CanonicalName("cafe\u0301"));
        var source = Synthetic.Source();
        var selection = new SourceSelection(source.Id, source.Binding,
            [new(SelectorKind.Query, query: new(QueryKind.Equal, "Demo.Choice", FieldValue.Bool(false)))]);
        var id = Guid.NewGuid();
        var catalogId = Guid.NewGuid();
        var first = new CreateBenchCommand(id, "Synthetic definition", catalogId, 0, [selection]);
        var retry = new CreateBenchCommand(id, "Synthetic definition", catalogId, 45, [selection]);
        Equal(first.Fingerprint, retry.Fingerprint);
        var changed = new CreateBenchCommand(id, "Synthetic definition", catalogId, 0,
            [new(source.Id, source.Binding, [new(SelectorKind.Query, query: new(QueryKind.Equal, "Demo.Choice", FieldValue.Null))])]);
        Check(first.Fingerprint != changed.Fingerprint, "Typed null and Boolean collapsed.");
        return Task.CompletedTask;
    }),
    ("real catalog creates empty Bench without native source or context effects", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var reader = new SyntheticReader();
        var app = new BenchCatalog(catalog.Store, reader, new());
        var command = Required(await app.PrepareCreateAsync(Guid.NewGuid(), "Empty synthetic Bench", catalog.Id, 0, [], default));
        var receipt = Required(await app.CreateAsync(command, default));
        Equal(1L, receipt.Definition.Revision);
        var view = Required(await app.ReadAsync(receipt.Definition.Id, default));
        Equal(0, view.Sources.Length);
        Equal(0, reader.ReadCount);
        Equal(1, Required(await catalog.Store.ReadAsync(default)).Benches.Length);
    }),
    ("same-path same-revision replacement rejects old empty-create and source-registration captures atomically", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var old = new CreateBenchCommand(Guid.NewGuid(), "Captured empty synthetic", catalog.Id, 0, []);
        var replacement = await catalog.ReplaceAtSamePath();
        var before = Required(await replacement.ReadAsync(default));
        Check(before.Id != catalog.Id, "The replacement did not change catalog identity.");
        Equal(0L, before.Revision);
        Equal(ErrorCode.Conflict, (await catalog.Store.CreateAsync(old, default)).Error.Code);
        Equal(ErrorCode.Conflict, (await catalog.Store.RegisterAsync(Synthetic.Source(), catalog.Id, 0, default)).Error.Code);
        var after = Required(await replacement.ReadAsync(default));
        Equal(0L, after.Revision);
        Equal(0, after.Benches.Length);
        Equal(0, after.Sources.Length);
    }),
    ("creation UUID replay cannot adopt a receipt from a replacement catalog at the same revision", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var command = new CreateBenchCommand(Guid.NewGuid(), "Synthetic replacement receipt", catalog.Id, 0, []);
        var oldReceipt = Required(await catalog.Store.CreateAsync(command, default)).Receipt;
        var replacement = await catalog.ReplaceAtSamePath();
        var replacementId = Required(await replacement.ReadAsync(default)).Id;
        var newCommand = new CreateBenchCommand(command.Definition.Id, command.Definition.Name, replacementId, 0, []);
        var current = Required(await replacement.CreateAsync(newCommand, default)).Receipt;
        Equal(oldReceipt.CatalogRevision, current.CatalogRevision);
        Equal(ErrorCode.Conflict, (await catalog.Store.CreateAsync(command, default)).Error.Code);
        var replay = Required(await replacement.CreateAsync(newCommand, default)).Receipt;
        Equal(replacementId, replay.CatalogId);
        Equal(1, Required(await replacement.ReadAsync(default)).Benches.Length);
    }),
    ("SQLite canonical uniqueness and concurrent catalog CAS allow only one creation", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
            await catalog.Store.CreateAsync(new(Guid.NewGuid(), n % 2 == 0 ? "Caf\u00e9" : "cafe\u0301", catalog.Id, 0, []), default))));
        Equal(1, results.Count(r => r.IsSuccess));
        Check(results.Where(r => !r.IsSuccess).All(r => r.Error.Code == ErrorCode.Conflict), "Concurrent failure was hidden.");
        var snapshot = Required(await catalog.Store.ReadAsync(default));
        Equal(1L, snapshot.Revision);
        var duplicate = await catalog.Store.CreateAsync(new(Guid.NewGuid(), "CAF\u00c9", catalog.Id, snapshot.Revision, []), default);
        Equal(ErrorCode.Conflict, duplicate.Error.Code);
    }),
    ("receipt replay preserves original result and different intent conflicts across reopen", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var command = new CreateBenchCommand(Guid.NewGuid(), "Synthetic replay", catalog.Id, 0, []);
        var first = Required(await catalog.Store.CreateAsync(command, default));
        Equal(true, first.Committed);
        Required(await catalog.Store.CreateAsync(new(Guid.NewGuid(), "Another synthetic Bench", catalog.Id, 1, []), default));
        var reopened = Required(await SqliteCatalog.OpenAsync(catalog.Path));
        var replay = Required(await reopened.CreateAsync(new(command.Definition.Id, command.Definition.Name, catalog.Id, 2, []), default));
        Equal(false, replay.Committed);
        Equal(first.Receipt.CatalogRevision, replay.Receipt.CatalogRevision);
        Equal(first.Receipt.Fingerprint, replay.Receipt.Fingerprint);
        var conflict = await reopened.CreateAsync(new(command.Definition.Id, "Different intent", catalog.Id, 2, []), default);
        Equal(ErrorCode.Conflict, conflict.Error.Code);
        Equal(2, Required(await reopened.ReadAsync(default)).Benches.Length);
    }),
    ("stale precommit failure keeps UUID usable with a refreshed revision", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        Required(await catalog.Store.CreateAsync(new(Guid.NewGuid(), "Earlier", catalog.Id, 0, []), default));
        var id = Guid.NewGuid();
        Equal(ErrorCode.Conflict, (await catalog.Store.CreateAsync(new(id, "Later", catalog.Id, 0, []), default)).Error.Code);
        var committed = Required(await catalog.Store.CreateAsync(new(id, "Later", catalog.Id, 1, []), default));
        Equal(id, committed.Receipt.CommandId);
        Equal(2L, committed.Receipt.CatalogRevision);
    }),
    ("catalog round-trips every Core query value kind without flattening typed selectors", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var source = Synthetic.Source();
        Required(await catalog.Store.RegisterAsync(source, catalog.Id, 0, default));
        var values = new[]
        {
            FieldValue.Null, FieldValue.String(""), FieldValue.RichText("<b>synthetic</b>", FieldKind.Html),
            FieldValue.RichText("**synthetic**", FieldKind.Markdown), FieldValue.Integer(long.MaxValue),
            FieldValue.Decimal(123456789.123456789m), FieldValue.Bool(false),
            FieldValue.At(new(2025, 1, 2, 12, 30, 0, TimeSpan.FromHours(2))),
            FieldValue.Identity("synthetic-identity"), FieldValue.Strings(["a", "b"]), FieldValue.Raw("{}", "synthetic-json"),
        };
        var selectors = values.Select(v => new Selector(SelectorKind.Query, query: new(QueryKind.Equal, "Demo.Value", v))).ToArray();
        var command = new CreateBenchCommand(Guid.NewGuid(), "Typed synthetic", catalog.Id, 1,
            [new(source.Id, source.Binding, selectors)]);
        Required(await catalog.Store.CreateAsync(command, default));
        var reopened = Required(await SqliteCatalog.OpenAsync(catalog.Path));
        var bench = Required(await reopened.ReadAsync(default)).Benches.Single();
        Equal(command.Fingerprint, CatalogFingerprint.Creation(catalog.Id, bench));
        var retained = bench.Sources.Single().Selectors;
        Equal(values.Length, retained.Length);
        for (var index = 0; index < values.Length; index++)
            Check(values[index].Equals(retained[index].Query!.Value), "Persisted Core value changed kind, precision or payload.");
    }),
    ("postcommit response loss recovers one durable receipt after an owned process exits", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var id = Guid.NewGuid();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(TestCatalog).Assembly.Location);
        start.ArgumentList.Add("--commit-without-response");
        start.ArgumentList.Add(catalog.Path);
        start.ArgumentList.Add(id.ToString("D"));
        start.ArgumentList.Add(catalog.Id.ToString("D"));
        using var child = Process.Start(start) ?? throw new Exception("Test child did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await child.WaitForExitAsync(deadline.Token); }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Equal(23, child.ExitCode);
        var recovered = Required(await catalog.Store.CreateAsync(new(id, "Synthetic crash receipt", catalog.Id, 0, []), default));
        Equal(false, recovered.Committed);
        Equal(1, Required(await catalog.Store.ReadAsync(default)).Benches.Length);
    }),
    ("registration rejects duplicate admitted bindings without adopting another native locator", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var source = Synthetic.Source();
        Required(await catalog.Store.RegisterAsync(source, catalog.Id, 0, default));
        var duplicate = new RegisteredSource(Guid.NewGuid(), "Different label", "different-opaque-locator",
            source.Binding, false, source.AdmittedOnlineAt);
        Equal(ErrorCode.Conflict, (await catalog.Store.RegisterAsync(duplicate, catalog.Id, 1, default)).Error.Code);
        Equal(1, Required(await catalog.Store.ReadAsync(default)).Sources.Length);
    }),
    ("copy-on-create persists independent Core selectors and leaves native Bench/journal untouched", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var source = Synthetic.Source();
        var item = Synthetic.Item(source, "42");
        var native = new CoreBench(Guid.NewGuid(), "Synthetic native", [new(SelectorKind.Pin, item.Key)]);
        var original = Synthetic.Observation(source, [item], benches: [native]);
        var reader = new SyntheticReader();
        reader.Set(source, original);
        var changes = new CatalogChanges();
        using var subscription = changes.Subscribe(out var invalidations);
        var app = new BenchCatalog(catalog.Store, reader, changes);
        Required(await catalog.Store.RegisterAsync(source, catalog.Id, 0, default));
        var frozen = Required(await app.PrepareCreateAsync(Guid.NewGuid(), "Copied synthetic", catalog.Id, 1,
            [new(source.Id, native.Id)], default));
        reader.Set(source, Synthetic.Observation(source, [item],
            benches: [new(native.Id, "Renamed later", [])]));
        var receipt = Required(await app.CreateAsync(frozen, default));
        var reopened = Required(await SqliteCatalog.OpenAsync(catalog.Path));
        var stored = Required(await reopened.ReadAsync(default)).Benches.Single();
        Equal(frozen.Fingerprint, CatalogFingerprint.Creation(catalog.Id, stored));
        Equal(item.Key, stored.Sources.Single().Selectors.Single().Item);
        Equal(1, original.Snapshot.Benches.Single().Selectors.Length);
        Equal(0, original.Snapshot.Journal.Length);
        Equal(0, original.Snapshot.Plans.Length);
        Equal(receipt.CatalogRevision, (await invalidations.ReadAsync()).Revision);
        Required(await app.CreateAsync(frozen, default));
        Equal(false, invalidations.TryRead(out _));
    }),
    ("source-qualified equal numeric IDs survive independent source failures and real Core membership", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var one = Synthetic.Source("ado");
        var two = Synthetic.Source("github");
        var reader = new SyntheticReader();
        reader.Set(one, Synthetic.Observation(one, [Synthetic.Item(one, "42")]));
        reader.Set(two, Synthetic.Observation(two, [Synthetic.Item(two, "42")]));
        Required(await catalog.Store.RegisterAsync(one, catalog.Id, 0, default));
        Required(await catalog.Store.RegisterAsync(two, catalog.Id, 1, default));
        var definition = new CreateBenchCommand(Guid.NewGuid(), "Federated synthetic", catalog.Id, 2,
            [Synthetic.All(one), Synthetic.All(two)]);
        Required(await catalog.Store.CreateAsync(definition, default));
        var app = new BenchCatalog(catalog.Store, reader, new());
        var both = Required(await app.ReadAsync(definition.Definition.Id, default));
        Equal(2, both.Sources.SelectMany(s => s.Items).Select(i => i.Identity).Distinct().Count());
        reader.Fail(two, new(ErrorCode.PolicyViolation, "Synthetic known denial"));
        var partial = Required(await app.ReadAsync(definition.Definition.Id, default));
        Equal(1, partial.Sources.Single(s => s.SourceId == one.Id).Items.Length);
        Equal(ErrorCode.PolicyViolation, partial.Sources.Single(s => s.SourceId == two.Id).Error!.Code);
    }),
    ("unknown membership is an unavailable source, never manufactured empty membership", () =>
    {
        var source = Synthetic.Source();
        var selected = new SourceSelection(source.Id, source.Binding,
            [new(SelectorKind.Query, query: new(QueryKind.Equal, "Demo.NotObserved", FieldValue.String("x")))]);
        var definition = new BenchDefinition(Guid.NewGuid(), "Unknown synthetic", 1, [selected]);
        var projection = SourceProjection.Create(definition, selected, Synthetic.Observation(source, [Synthetic.Item(source, "1")]));
        Equal(ErrorCode.Unavailable, projection.Error.Code);
        return Task.CompletedTask;
    }),
    ("typed grouping retains null, absent, out-of-catalog, Boolean false and composite list values", () =>
    {
        var source = Synthetic.Source();
        var rows = new[]
        {
            Synthetic.Item(source, "1", FieldValues.Empty.Set("Demo.Workflow", FieldValue.String("Example active"))
                .Set("Demo.Flag", FieldValue.Bool(false)).Set("Demo.Tags", FieldValue.Strings(["a", "b"]))),
            Synthetic.Item(source, "2", FieldValues.Empty.Set("Demo.Workflow", FieldValue.Null)
                .Set("Demo.Flag", FieldValue.Bool(true)).Set("Demo.Tags", FieldValue.Strings([]))),
            Synthetic.Item(source, "3", FieldValues.Empty),
            Synthetic.Item(source, "4", FieldValues.Empty.Set("Demo.Workflow", FieldValue.String("Unexpected"))
                .Set("Demo.Flag", FieldValue.Null)),
        };
        var view = Synthetic.Project(source, Synthetic.Observation(source, rows));
        var state = SourceProjection.Group(view, "Demo.Workflow");
        Equal(4, state.Sum(g => g.Members.Length));
        Equal(1, state.Single(g => !g.Observed).Members.Length);
        Equal(1, state.Single(g => g.Value?.Kind == FieldKind.Null).Members.Length);
        Equal(1, state.Single(g => g.Value?.Text == "Unexpected" && !g.InCatalog).Members.Length);
        var flags = SourceProjection.Group(view, "Demo.Flag");
        Equal(1, flags.Single(g => g.Value?.Kind == FieldKind.Boolean && !g.Value.Boolean).Members.Length);
        var tags = SourceProjection.Group(view, "Demo.Tags");
        Equal(4, tags.Sum(g => g.Members.Length));
        Equal(2, tags.Count(g => g.Composite));
        Check(view.Fields.All(f => f.SourceId == source.Id), "Field provenance collapsed across sources.");
        return Task.CompletedTask;
    }),
    ("mapped local keys, protected draft debt, and proposed null survive projection", () =>
    {
        var source = Synthetic.Source();
        var item = Synthetic.Item(source, "42", FieldValues.Empty.Set("Demo.Workflow", FieldValue.String("Example active")));
        var local = ItemKey.Local(Guid.NewGuid());
        var outside = ItemKey.Local(Guid.NewGuid());
        var draft = new LocalEdits(item.Key, item.Type, item, FieldValues.Empty.Set("Demo.Workflow", FieldValue.Null));
        var debt = new Seed(outside, item.Type, FieldValues.Empty.Set("Demo.Workflow", FieldValue.String("Example active")));
        var plan = new Plan(source.Binding.Connection, "schema", 1, [new("create", OpKind.Create, local,
            fields: item.Fields, type: item.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        var applying = Required(JournalEntry.Planned(plan.Digest, 0).Begin(Guid.NewGuid()));
        var applied = Required(applying.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Applied, ["accepted"], item.Revision, item.Key)));
        var verified = Required(applied.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Verified, ["read back"], item.Revision, item.Key)));
        var observation = Synthetic.Observation(source, [item], retainedItems: [draft, debt],
            mappings: [new(local, item.Key, plan.Digest, 0)], plans: [plan], journal: [verified]);
        var selection = new SourceSelection(source.Id, source.Binding, [new(SelectorKind.Pin, local)]);
        var projected = Required(SourceProjection.Create(new(Guid.NewGuid(), "Mapped synthetic", 1, [selection]), selection, observation));
        Equal(2, projected.Items.Length);
        Equal(item.Key, projected.Items.Single(i => i.BenchMember).Identity.Key);
        Equal(FieldKind.Null, projected.Items.Single(i => i.BenchMember).EffectiveFields["Demo.Workflow"].Kind);
        Equal(false, projected.Items.Single(i => i.Identity.Key == outside).BenchMember);
        return Task.CompletedTask;
    }),
    ("hierarchy cycles disable only tree; tombstones and outside context retain provenance", () =>
    {
        var source = Synthetic.Source();
        var a = Synthetic.Item(source, "1");
        var b = Synthetic.Item(source, "2");
        var outside = ItemKey.Remote(source.Binding.Connection.Provider, source.Binding.Connection.ResourceScope, "99");
        var cycle = Synthetic.Project(source, Synthetic.Observation(source, [a, b],
            edges: [new(a.Key, b.Key, "Demo.Parent", RelationshipOrigin.Provider),
                new(b.Key, a.Key, "Demo.Parent", RelationshipOrigin.Local, 7)]));
        Equal(false, cycle.Hierarchy.Available);
        Equal(2, cycle.Items.Length);
        var cached = Synthetic.Project(source, Synthetic.Observation(source, [a, b],
            edges: [new(a.Key, b.Key, "Demo.Parent", RelationshipOrigin.Provider),
                new(b.Key, a.Key, "Demo.Parent", RelationshipOrigin.Local, 8, true),
                new(a.Key, outside, "Demo.Parent", RelationshipOrigin.Provider)]));
        Equal(true, cached.Hierarchy.Available);
        Check(cached.Hierarchy.Evidence.Any(e => e.Deleted && e.LocalVersion == 8), "A tombstone was discarded.");
        Check(cached.Hierarchy.Limitations.Length > 0, "Outside context was not labelled.");
        return Task.CompletedTask;
    }),
    ("binding replacement, unapproved offline use and known denial block content without prose inference", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var source = Synthetic.Source();
        var reader = new SyntheticReader();
        var observation = Synthetic.Observation(source, [Synthetic.Item(source, "1")]);
        reader.Set(source, observation);
        Required(await catalog.Store.RegisterAsync(source, catalog.Id, 0, default));
        var definition = new CreateBenchCommand(Guid.NewGuid(), "Admission synthetic", catalog.Id, 1, [Synthetic.All(source)]);
        Required(await catalog.Store.CreateAsync(definition, default));
        var app = new BenchCatalog(catalog.Store, reader, new());
        reader.Set(source, observation with { Mode = ObservationMode.PermittedOfflineCache });
        Equal(ErrorCode.Conflict, Required(await app.ReadAsync(definition.Definition.Id, default)).Sources.Single().Error!.Code);
        reader.Set(source, observation with { Binding = new(source.Binding.Connection, Guid.NewGuid(), source.Binding.NativeStamp) });
        Equal(ErrorCode.Conflict, Required(await app.ReadAsync(definition.Definition.Id, default)).Sources.Single().Error!.Code);
        reader.Fail(source, new(ErrorCode.PolicyViolation, "A synthetic provider denied this request."));
        Equal(ErrorCode.PolicyViolation, (await app.CreateAsync(definition, default)).Error.Code);
    }),
    ("previously online admitted source with explicit offline policy retains labelled cache, not fresh authority", async () =>
    {
        await using var catalog = await TestCatalog.Create();
        var source = Synthetic.Source(offline: true);
        var reader = new SyntheticReader();
        reader.Set(source, Synthetic.Observation(source, [Synthetic.Item(source, "1")]) with
        { Mode = ObservationMode.PermittedOfflineCache });
        Required(await catalog.Store.RegisterAsync(source, catalog.Id, 0, default));
        var command = new CreateBenchCommand(Guid.NewGuid(), "Offline synthetic", catalog.Id, 1, [Synthetic.All(source)]);
        Required(await catalog.Store.CreateAsync(command, default));
        var view = Required(await new BenchCatalog(catalog.Store, reader, new()).ReadAsync(command.Definition.Id, default));
        Equal(ObservationMode.PermittedOfflineCache, view.Sources.Single().Observation!.Mode);
        Equal(1, view.Sources.Single().Items.Length);
        Check(view.Sources.Single().Limitations.Any(n => n.StartsWith("Offline", StringComparison.Ordinal)), "Offline cache was not labelled.");
    }),
    ("catalog never adopts/replaces existing noncatalog or native-looking files", async () =>
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bench-not-catalog-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(path, "Synthetic noncatalog evidence");
        try
        {
            Equal(ErrorCode.StorageUnavailable, (await SqliteCatalog.CreateAsync(path)).Error.Code);
            Equal(ErrorCode.StorageUnavailable, (await SqliteCatalog.OpenAsync(path)).Error.Code);
            Equal("Synthetic noncatalog evidence", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }),
};

var passed = 0;
foreach (var (name, run) in tests)
{
    await run();
    passed++;
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{passed}/{tests.Length} catalog/application tests passed (synthetic external source boundary; real Core and SQLite).");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; received {actual}.");
}
static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new Exception($"Unexpected native error: {result.Error.Code}");

sealed class TestCatalog(string path, SqliteCatalog store, Guid id) : IAsyncDisposable
{
    public string Path { get; } = path;
    public SqliteCatalog Store { get; } = store;
    public Guid Id { get; } = id;
    public static async Task<TestCatalog> Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bench-catalog-tests-{Guid.NewGuid():N}.db");
        var created = await SqliteCatalog.CreateAsync(path);
        if (!created.IsSuccess) throw new Exception($"Cannot create test catalog: {created.Error.Code}");
        var snapshot = await created.Value.ReadAsync(default);
        if (!snapshot.IsSuccess) throw new Exception("Cannot read the new test catalog.");
        return new(path, created.Value, snapshot.Value.Id);
    }
    public async Task<SqliteCatalog> ReplaceAtSamePath()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(Path + suffix)) File.Move(Path + suffix, Path + ".replaced" + suffix);
        var created = await SqliteCatalog.CreateAsync(Path);
        return created.IsSuccess ? created.Value : throw new Exception("Test replacement catalog failed.");
    }
    public ValueTask DisposeAsync()
    {
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm",
            Path + ".replaced", Path + ".replaced-wal", Path + ".replaced-shm" })
            if (File.Exists(file)) File.Delete(file);
        return ValueTask.CompletedTask;
    }
}

sealed class SyntheticReader : ISourceReader
{
    private readonly Dictionary<string, Result<SourceObservation>> results = [];
    public int ReadCount { get; private set; }
    public void Set(RegisteredSource source, SourceObservation value) => results[source.NativeLocator] = Result<SourceObservation>.Ok(value);
    public void Fail(RegisteredSource source, Error error) => results[source.NativeLocator] = Result<SourceObservation>.Fail(error);
    public Task<Result<SourceObservation>> AdmitAsync(string nativeLocator, CancellationToken ct) => Task.FromResult(results[nativeLocator]);
    public Task<Result<SourceObservation>> ReadAsync(RegisteredSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult(results[source.NativeLocator]);
    }
}

static class Synthetic
{
    public static RegisteredSource Source(string provider = "ado", bool offline = false)
    {
        var id = Guid.NewGuid();
        return new(id, "Synthetic " + provider, "synthetic-locator-" + id.ToString("N"),
            new(new(new(provider), "synthetic-scope", "synthetic-target", "synthetic-actor"),
                Guid.NewGuid(), "synthetic-native-generation"), offline, DateTimeOffset.UtcNow);
    }
    public static WorkItem Item(RegisteredSource source, string id, FieldValues? fields = null)
    {
        var key = ItemKey.Remote(source.Binding.Connection.Provider, source.Binding.Connection.ResourceScope, id);
        return new(key, "SyntheticWork", fields ?? FieldValues.Empty.Set("Demo.Workflow", FieldValue.String("Example active")),
            new(key, "synthetic-revision"), relationshipsComplete: true);
    }
    public static SourceSelection All(RegisteredSource source)
        => new(source.Id, source.Binding, [new(SelectorKind.Query, query: new(QueryKind.All))]);
    public static SourceObservation Observation(RegisteredSource source, WorkItem[] items, Relationship[]? edges = null,
        RetainedItem[]? retainedItems = null, PublishMapping[]? mappings = null, CoreBench[]? benches = null,
        Plan[]? plans = null, JournalEntry[]? journal = null)
    {
        var type = new ItemType("SyntheticWork", "Demo.Workflow",
            [new("Demo.Workflow", FieldKind.Text), new("Demo.Flag", FieldKind.Boolean), new("Demo.Tags", FieldKind.StringList)],
            [new("Example active", StateCategory.InProgress)], [],
            [new("Demo.Tags", RuleAction.Allowed, [], [FieldValue.Strings(["a", "b"]), FieldValue.Strings([])])]);
        return new(source.Binding, new(5, items, edges, retainedItems, plans, journal, mappings, benches),
            new(source.Binding.Connection, [type], [new("Demo.Parent", RelationShape.Hierarchy, true)]),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ObservationMode.OnlineMetadata);
    }
    public static SourceView Project(RegisteredSource source, SourceObservation observation)
    {
        var selection = All(source);
        var projection = SourceProjection.Create(new(Guid.NewGuid(), "Synthetic projection", 1, [selection]), selection, observation);
        return projection.IsSuccess ? projection.Value : throw new Exception($"Projection failed: {projection.Error.Code}");
    }
}
